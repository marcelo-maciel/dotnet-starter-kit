#:package AWSSDK.S3@4.0.103.4
#:package Npgsql@10.0.0

// Restore check of RNF14 (M11): every media reference of the restored database points to an object that exists in the
// application bucket of the environment. Keys come from paf."Midias"."ChaveObjeto" (raw SQL, so every tenant of the
// database is covered; the EF tenant filter does not apply), one HEAD per distinct key, in parallel with a deadline.
//
// Usage: PAF_CONFERENCIA_CONEXAO='<connection string of the restored database>' \
//        dotnet run deploy/paf/r2/conferir-midias.cs -- <arquivo .env do ambiente>
// Exit: 0 every object present; 1 at least one object missing (listed); 2 the check itself could not run
// (environment file, database, credentials, timeout or any S3 error other than 404). Never prints secrets.

using System.Collections.Concurrent;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;

const int Paralelismo = 8;
var prazoHead = TimeSpan.FromSeconds(10);
var prazoTotal = TimeSpan.FromMinutes(30);

if (args.Length != 1)
{
    Console.Error.WriteLine("uso: dotnet run deploy/paf/r2/conferir-midias.cs -- <arquivo .env do ambiente>");
    return 2;
}

var conexao = Environment.GetEnvironmentVariable("PAF_CONFERENCIA_CONEXAO");
if (string.IsNullOrWhiteSpace(conexao))
{
    Console.Error.WriteLine("ERRO: PAF_CONFERENCIA_CONEXAO vazia (string de conexão do banco restaurado)");
    return 2;
}

Dictionary<string, string> ambiente;
try
{
    ambiente = LerArquivoAmbiente(args[0]);
}
catch (IOException ex)
{
    Console.Error.WriteLine($"ERRO: arquivo do ambiente ilegível: {ex.Message}");
    return 2;
}

string[] obrigatorias = ["Storage__S3__ServiceUrl", "Storage__S3__Bucket", "Storage__S3__AccessKey", "Storage__S3__SecretKey"];
var faltando = obrigatorias.Where(c => !ambiente.TryGetValue(c, out var v) || string.IsNullOrWhiteSpace(v) || v.Contains("${", StringComparison.Ordinal) || v.StartsWith('<')).ToList();
if (faltando.Count > 0)
{
    Console.Error.WriteLine($"ERRO: sem valor literal no arquivo do ambiente: {string.Join(", ", faltando)}");
    return 2;
}

var bucket = ambiente["Storage__S3__Bucket"];
var prefixo = ambiente.GetValueOrDefault("Storage__S3__Prefix", "");
using var total = new CancellationTokenSource(prazoTotal);

List<string> chaves;
try
{
    var construtor = new NpgsqlConnectionStringBuilder(conexao) { Timeout = 15, CommandTimeout = 120 };
    await using var banco = new NpgsqlConnection(construtor.ConnectionString);
    await banco.OpenAsync(total.Token);
    await using var comando = new NpgsqlCommand("""SELECT DISTINCT "ChaveObjeto" FROM paf."Midias" ORDER BY 1""", banco);
    await using var leitor = await comando.ExecuteReaderAsync(total.Token);
    chaves = [];
    while (await leitor.ReadAsync(total.Token))
    {
        chaves.Add(leitor.GetString(0));
    }
}
catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException or ArgumentException)
{
    Console.Error.WriteLine($"ERRO: leitura do banco restaurado falhou: {ex.GetType().Name}: {ex.Message}");
    return 2;
}

var configuracao = new AmazonS3Config
{
    ServiceURL = ambiente["Storage__S3__ServiceUrl"],
    ForcePathStyle = true,
    AuthenticationRegion = ambiente.GetValueOrDefault("Storage__S3__Region", "auto"),
    Timeout = prazoHead,
    MaxErrorRetry = 2,
};
using var s3 = new AmazonS3Client(new BasicAWSCredentials(ambiente["Storage__S3__AccessKey"], ambiente["Storage__S3__SecretKey"]), configuracao);

var ausentes = new ConcurrentBag<string>();
var erros = new ConcurrentBag<string>();
try
{
    await Parallel.ForEachAsync(chaves, new ParallelOptions { MaxDegreeOfParallelism = Paralelismo, CancellationToken = total.Token }, async (chave, ct) =>
    {
        var chaveFinal = ChaveFinal(chave, prefixo);
        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(ct);
        prazo.CancelAfter(prazoHead);
        try
        {
            await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = chaveFinal }, prazo.Token);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            ausentes.Add(chaveFinal);
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or OperationCanceledException or HttpRequestException)
        {
            erros.Add($"{chaveFinal}: {ex.GetType().Name} {(ex as AmazonServiceException)?.StatusCode}");
        }
    });
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine($"ERRO: conferência passou do prazo total de {prazoTotal.TotalMinutes} min");
    return 2;
}

foreach (var chave in ausentes.Order(StringComparer.Ordinal))
{
    Console.WriteLine($"AUSENTE {chave}");
}

foreach (var erro in erros.Order(StringComparer.Ordinal))
{
    Console.WriteLine($"ERRO {erro}");
}

Console.WriteLine($"RESUMO referencias={chaves.Count} presentes={chaves.Count - ausentes.Count - erros.Count} ausentes={ausentes.Count} erros={erros.Count} bucket={bucket}");
if (!erros.IsEmpty)
{
    return 2;
}

return ausentes.IsEmpty ? 0 : 1;

// Same rule as the kit's S3StorageService.NormalizeKey, so the HEAD hits the key the API wrote, including its quirk:
// a key that already starts with the prefix text (case-insensitive) is not prefixed again.
static string ChaveFinal(string chave, string prefixo)
{
    var aparada = chave.TrimStart('/');
    if (string.IsNullOrWhiteSpace(prefixo))
    {
        return aparada;
    }

    return aparada.StartsWith(prefixo, StringComparison.OrdinalIgnoreCase) ? aparada : $"{prefixo.TrimEnd('/')}/{aparada}";
}

// Plain KEY=VALUE lines, the same file the compose passes as env_file to the API. Interpolated values (${...}) are kept
// as text and rejected by the caller for the keys it needs, because only the compose resolves them.
static Dictionary<string, string> LerArquivoAmbiente(string caminho)
{
    var valores = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var linha in File.ReadAllLines(caminho))
    {
        var texto = linha.Trim();
        if (texto.Length == 0 || texto.StartsWith('#'))
        {
            continue;
        }

        var igual = texto.IndexOf('=', StringComparison.Ordinal);
        if (igual > 0)
        {
            valores[texto[..igual].Trim()] = texto[(igual + 1)..].Trim();
        }
    }

    return valores;
}
