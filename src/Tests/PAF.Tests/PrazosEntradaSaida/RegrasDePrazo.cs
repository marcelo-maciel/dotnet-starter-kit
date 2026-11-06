using Amazon.Runtime;
using Npgsql;
using StackExchange.Redis;

namespace PAF.Tests.PrazosEntradaSaida;

/// <summary>
/// RNF05 rules over already-resolved values: each check returns the violations, empty when the value complies.
/// A value counts as explicit only when it was written, because every client below falls back to a default
/// when the key is absent and the default is what RNF05 rules out.
/// </summary>
internal static class RegrasDePrazo
{
    // Single ceiling for every dependency (O19): above it a stalled dependency holds the request longer
    // than the client's own retry would, so a "prazo explícito" of minutes is no prazo at all.
    internal static readonly TimeSpan Teto = TimeSpan.FromSeconds(30);

    private static readonly string[] ChavesPostgres = ["Timeout", "Command Timeout"];
    private static readonly string[] ChavesValkey = ["connectTimeout", "syncTimeout", "asyncTimeout"];

    internal static List<string> Postgres(string origem, string stringDeConexao)
    {
        var violacoes = new List<string>();
        var conexao = new NpgsqlConnectionStringBuilder(stringDeConexao);
        foreach (string chave in ChavesPostgres)
        {
            // ShouldSerialize is true only for keys present in the string; the typed properties return the defaults.
            if (!conexao.ShouldSerialize(chave))
            {
                violacoes.Add($"{origem}: '{chave}' ausente da string de conexão do PostgreSQL");
            }
        }

        // Npgsql reads 0 as "wait forever" for both keys.
        ConferirSegundos(violacoes, $"{origem}: Timeout", conexao.Timeout);
        ConferirSegundos(violacoes, $"{origem}: Command Timeout", conexao.CommandTimeout);
        return violacoes;
    }

    internal static List<string> Valkey(string origem, string configuracao)
    {
        var violacoes = new List<string>();
        var opcoes = ConfigurationOptions.Parse(configuracao);

        // ToString writes back only the options that were set, so it is the record of what was explicit.
        var escritas = opcoes.ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(parte => parte.Split('=')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string chave in ChavesValkey.Where(c => !escritas.Contains(c)))
        {
            violacoes.Add($"{origem}: '{chave}' ausente da configuração do Valkey");
        }

        ConferirMilissegundos(violacoes, $"{origem}: connectTimeout", opcoes.ConnectTimeout);
        ConferirMilissegundos(violacoes, $"{origem}: syncTimeout", opcoes.SyncTimeout);
        ConferirMilissegundos(violacoes, $"{origem}: asyncTimeout", opcoes.AsyncTimeout);
        return violacoes;
    }

    internal static List<string> HttpClientNomeado(string nome, TimeSpan prazo)
    {
        var violacoes = new List<string>();
        ConferirPrazo(violacoes, $"HttpClient '{nome}': Timeout", prazo);
        return violacoes;
    }

    internal static List<string> ClienteAws(string origem, IClientConfig configuracao)
    {
        var violacoes = new List<string>();
        // AWSSDK 4 reports an unset Timeout as int.MaxValue milliseconds (about 24 days), so the ceiling catches
        // it; a null is treated as the same unbounded wait.
        ConferirPrazo(violacoes, $"{origem}: Timeout", configuracao.Timeout ?? Timeout.InfiniteTimeSpan);
        return violacoes;
    }

    private static void ConferirSegundos(List<string> violacoes, string alvo, int segundos)
    {
        ConferirPrazo(violacoes, alvo, segundos == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(segundos));
    }

    private static void ConferirMilissegundos(List<string> violacoes, string alvo, int milissegundos)
    {
        ConferirPrazo(violacoes, alvo, TimeSpan.FromMilliseconds(milissegundos));
    }

    private static void ConferirPrazo(List<string> violacoes, string alvo, TimeSpan prazo)
    {
        if (prazo == Timeout.InfiniteTimeSpan)
        {
            violacoes.Add($"{alvo} infinito");
        }
        else if (prazo <= TimeSpan.Zero)
        {
            violacoes.Add($"{alvo} = {prazo}: não positivo");
        }
        else if (prazo > Teto)
        {
            violacoes.Add($"{alvo} = {prazo} acima do teto de {Teto}");
        }
    }
}
