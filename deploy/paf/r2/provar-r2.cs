#:package AWSSDK.S3@4.0.103.4

// Acceptance probe of M11 against the application bucket of one environment (homologação first): the 30-day lock covers
// paf-midia/ (second PUT and DELETE refused, content intact), objects outside it are deletable, an unsigned GET is refused (4xx),
// a signature longer than 7 days is refused while the same signer with 1 h is accepted (control), and the bucket CORS
// answers the dashboard origin for PUT and GET and nobody else.
//
// Usage: dotnet run deploy/paf/r2/provar-r2.cs -- <arquivo .env do ambiente>
// Exit 0 only when every step is PASS. Residue: one small object under paf-midia/_prova-m11/ that the lock keeps for
// 30 days (printed at the end); the object outside the prefix is deleted by the probe itself.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

var prazo = TimeSpan.FromSeconds(20);
const string PrefixoMidia = "paf-midia/";
const string TipoConteudo = "text/plain";

if (args.Length != 1)
{
    Console.Error.WriteLine("uso: dotnet run deploy/paf/r2/provar-r2.cs -- <arquivo .env do ambiente>");
    return 2;
}

var ambiente = LerArquivoAmbiente(args[0]);
string[] obrigatorias = ["Storage__S3__ServiceUrl", "Storage__S3__Bucket", "Storage__S3__AccessKey", "Storage__S3__SecretKey", "PAF_DASHBOARD_URL"];
var faltando = obrigatorias.Where(c => !ambiente.TryGetValue(c, out var v) || string.IsNullOrWhiteSpace(v) || v.Contains("${", StringComparison.Ordinal) || v.StartsWith('<')).ToList();
if (faltando.Count > 0)
{
    Console.Error.WriteLine($"ERRO: sem valor literal no arquivo do ambiente: {string.Join(", ", faltando)}");
    return 2;
}

var servico = new Uri(ambiente["Storage__S3__ServiceUrl"].TrimEnd('/'));
var bucket = ambiente["Storage__S3__Bucket"];
var regiao = ambiente.GetValueOrDefault("Storage__S3__Region", "auto");
var acesso = ambiente["Storage__S3__AccessKey"];
var segredo = ambiente["Storage__S3__SecretKey"];
var origemDashboard = ambiente["PAF_DASHBOARD_URL"].TrimEnd('/');
var prefixoKit = ambiente.GetValueOrDefault("Storage__S3__Prefix", "");

var configuracao = new AmazonS3Config { ServiceURL = servico.ToString(), ForcePathStyle = true, AuthenticationRegion = regiao, Timeout = prazo, MaxErrorRetry = 1 };
using var s3 = new AmazonS3Client(new BasicAWSCredentials(acesso, segredo), configuracao);
using var http = new HttpClient { Timeout = prazo };

var execucao = Guid.NewGuid().ToString("N");
var chaveTravada = ChaveFinal($"{PrefixoMidia}_prova-m11/{execucao}.txt", prefixoKit);
var chaveLivre = ChaveFinal($"_prova-m11/{execucao}.txt", prefixoKit);
var original = Encoding.UTF8.GetBytes($"original {execucao}");
var sobrescrita = Encoding.UTF8.GetBytes($"sobrescrita {execucao}");
var resultados = new List<(bool Passou, string Passo)>();

void Registrar(bool passou, string passo)
{
    resultados.Add((passou, passo));
    Console.WriteLine($"{(passou ? "PASS" : "FAIL")} {passo}");
}

async Task<HttpStatusCode> PutAssinadoAsync(string chave, byte[] corpo)
{
    var url = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
    {
        BucketName = bucket, Key = chave, Verb = HttpVerb.PUT, ContentType = TipoConteudo,
        Expires = DateTime.UtcNow.AddMinutes(15), Protocol = servico.Scheme == "https" ? Protocol.HTTPS : Protocol.HTTP,
    });
    using var conteudo = new ByteArrayContent(corpo);
    conteudo.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(TipoConteudo);
    using var resposta = await http.PutAsync(url, conteudo);
    return resposta.StatusCode;
}

async Task<(HttpStatusCode Status, byte[] Corpo)> GetAsync(string url)
{
    using var resposta = await http.GetAsync(url);
    return (resposta.StatusCode, await resposta.Content.ReadAsByteArrayAsync());
}

async Task<bool> ExisteAsync(string chave)
{
    try
    {
        await s3.GetObjectMetadataAsync(bucket, chave);
        return true;
    }
    catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
    {
        return false;
    }
    catch (AmazonS3Exception ex)
    {
        // Any other answer (a credential without access to the bucket) leaves the probe unable to decide: exit 2.
        Console.Error.WriteLine($"ERRO a conferência de existência não rodou: {(int)ex.StatusCode} {ex.ErrorCode}");
        Environment.Exit(2);
        throw;
    }
}

async Task<string> DeleteAsync(string chave)
{
    try
    {
        var resposta = await s3.DeleteObjectAsync(bucket, chave);
        return $"aceito {(int)resposta.HttpStatusCode}";
    }
    catch (AmazonS3Exception ex)
    {
        return $"recusado {(int)ex.StatusCode} {ex.ErrorCode}";
    }
}

Console.WriteLine($"--- prova M11 no bucket {bucket} ({servico.Host}), execução {execucao}");

// 1. Signed PUT under the media prefix, the path M03 hands to the dashboard.
var put1 = await PutAssinadoAsync(chaveTravada, original);
Registrar(put1 == HttpStatusCode.OK, $"PUT assinado em {PrefixoMidia}: {(int)put1}");

// 2. Second PUT on the same key: the lock blocks overwrite (REQ RF03 l.149); content must stay the original.
var put2 = await PutAssinadoAsync(chaveTravada, sobrescrita);
var lido = await GetAsync(UrlAssinada(chaveTravada, TimeSpan.FromHours(1)));
var intacto = lido.Status == HttpStatusCode.OK && lido.Corpo.AsSpan().SequenceEqual(original);
Registrar((int)put2 >= 400 && intacto, $"segundo PUT na mesma chave recusado e conteúdo intacto: PUT {(int)put2}, GET {(int)lido.Status}, intacto={intacto}");

// 3. Unsigned GET refused: the media is served only by short-lived signed URL (ADR 0007). R2 refuses with
// 400 InvalidArgument "Authorization" and other S3 stores with 403, so any 4xx that does not serve the content counts.
var semAssinatura = await GetAsync($"{servico}{bucket}/{CodificarChave(chaveTravada)}");
var semAssinaturaRecusado = (int)semAssinatura.Status is >= 400 and < 500 && !semAssinatura.Corpo.AsSpan().SequenceEqual(original);
Registrar(semAssinaturaRecusado, $"GET sem assinatura recusado: {(int)semAssinatura.Status}");

// 4. Signature lifetime: the same hand-made signer with 1 h is the control (proves the signer is right), with 8 days it
// must be refused by the server. Runs before the DELETE steps so the object still exists where the lock is absent.
var controle = await GetAsync(UrlAssinada(chaveTravada, TimeSpan.FromHours(1)));
var oitoDias = await GetAsync(UrlAssinada(chaveTravada, TimeSpan.FromDays(8)));
Registrar(controle.Status == HttpStatusCode.OK, $"controle do assinador: assinatura de 1 h aceita: {(int)controle.Status}");
Registrar(controle.Status == HttpStatusCode.OK && (int)oitoDias.Status >= 400, $"assinatura de 8 dias recusada pelo servidor: {(int)oitoDias.Status}");
// 5. DELETE inside the lock period refused; the object is still there afterwards.
var delTravado = await DeleteAsync(chaveTravada);
var aindaExiste = await ExisteAsync(chaveTravada);
Registrar(delTravado.StartsWith("recusado", StringComparison.Ordinal) && aindaExiste, $"DELETE em {PrefixoMidia} recusado: {delTravado}, objeto existe={aindaExiste}");

// 6. Outside the prefix the kit keeps deleting (avatar, logo, Files purge), so the lock must not reach it.
var putLivre = await PutAssinadoAsync(chaveLivre, original);
var delLivre = await DeleteAsync(chaveLivre);
var livreSumiu = !await ExisteAsync(chaveLivre);
Registrar(putLivre == HttpStatusCode.OK && delLivre.StartsWith("aceito", StringComparison.Ordinal) && livreSumiu, $"DELETE fora do prefixo aceito: PUT {(int)putLivre}, {delLivre}, sumiu={livreSumiu}");


// 7. Bucket CORS: the dashboard origin may PUT (upload, M03/M04) and GET (painel, M08); any other origin gets nothing.
async Task<string?> PreflightAsync(string origem, string metodo)
{
    using var pedido = new HttpRequestMessage(HttpMethod.Options, $"{servico}{bucket}/{CodificarChave(chaveTravada)}");
    pedido.Headers.Add("Origin", origem);
    pedido.Headers.Add("Access-Control-Request-Method", metodo);
    pedido.Headers.Add("Access-Control-Request-Headers", "content-type");
    using var resposta = await http.SendAsync(pedido);
    return resposta.IsSuccessStatusCode && resposta.Headers.TryGetValues("Access-Control-Allow-Origin", out var valores) ? valores.FirstOrDefault() : null;
}

foreach (var metodo in new[] { "PUT", "GET" })
{
    var permitido = await PreflightAsync(origemDashboard, metodo);
    Registrar(permitido == origemDashboard, $"CORS {metodo} a partir de {origemDashboard}: Allow-Origin={permitido ?? "(nenhum)"}");
}

var estranho = await PreflightAsync("https://origem-estranha.invalid", "PUT");
Registrar(estranho is null, $"CORS de origem estranha sem Allow-Origin: {estranho ?? "(nenhum)"}");

var falhas = resultados.Count(r => !r.Passou);
Console.WriteLine($"RESUMO passos={resultados.Count} falhas={falhas} residuo={bucket}/{chaveTravada}");
return falhas == 0 ? 0 : 1;

// Query-string SigV4 for GET, written here only because the SDK caps the lifetime at 7 days (AWS4 signing) and step 4 has to send a
// longer one to the server; the API never uses this (it signs through the kit's IStorageService).
string UrlAssinada(string chave, TimeSpan validade)
{
    var agora = DateTime.UtcNow;
    var data = agora.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    var instante = agora.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    var escopo = $"{data}/{regiao}/s3/aws4_request";
    var host = servico.IsDefaultPort ? servico.Host : $"{servico.Host}:{servico.Port}";
    var caminho = $"/{bucket}/{CodificarChave(chave)}";
    var consulta = string.Join("&", new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
        ["X-Amz-Credential"] = Uri.EscapeDataString($"{acesso}/{escopo}"),
        ["X-Amz-Date"] = instante,
        ["X-Amz-Expires"] = ((long)validade.TotalSeconds).ToString(CultureInfo.InvariantCulture),
        ["X-Amz-SignedHeaders"] = "host",
    }.Select(p => $"{p.Key}={p.Value}"));
    var canonico = $"GET\n{caminho}\n{consulta}\nhost:{host}\n\nhost\nUNSIGNED-PAYLOAD";
    var paraAssinar = $"AWS4-HMAC-SHA256\n{instante}\n{escopo}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonico)))}";
    var chaveAssinatura = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + segredo), data), regiao), "s3"), "aws4_request");
    var assinatura = Convert.ToHexStringLower(Hmac(chaveAssinatura, paraAssinar));
    return $"{servico.Scheme}://{host}{caminho}?{consulta}&X-Amz-Signature={assinatura}";
}

static byte[] Hmac(byte[] chave, string texto) => HMACSHA256.HashData(chave, Encoding.UTF8.GetBytes(texto));

static string CodificarChave(string chave) => string.Join("/", chave.Split('/').Select(Uri.EscapeDataString));

// Same rule as the kit's S3StorageService.NormalizeKey (see conferir-midias.cs).
static string ChaveFinal(string chave, string prefixo)
{
    var aparada = chave.TrimStart('/');
    if (string.IsNullOrWhiteSpace(prefixo))
    {
        return aparada;
    }

    return aparada.StartsWith(prefixo, StringComparison.OrdinalIgnoreCase) ? aparada : $"{prefixo.TrimEnd('/')}/{aparada}";
}

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
