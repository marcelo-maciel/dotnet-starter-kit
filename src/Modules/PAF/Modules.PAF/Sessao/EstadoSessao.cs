using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// Unidade corrente escolhida pela sessão, guardada no Valkey (ADR 0006). Sem Valkey registrado, desconectado
/// ou lento, responde como ausente: a unidade corrente cai na unidade da licença e a troca é recusada (seção 9).
/// </summary>
public sealed class EstadoSessao(IEnumerable<IConnectionMultiplexer> multiplexers, ILogger<EstadoSessao> logger)
{
    // ponytail: fixed per-call ceiling; tune once production Valkey latency is measured.
    private static readonly TimeSpan PrazoChamada = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Validade = TimeSpan.FromMinutes(Licencas.PrazoMarcaMinutos);

    private readonly IConnectionMultiplexer? _valkey = multiplexers.FirstOrDefault();

    public async Task<Guid?> LerUnidadeAsync(string tenantId, Guid sessaoId, CancellationToken ct)
    {
        var valor = await TentarAsync(sessaoId, db => db.StringGetAsync(Chave(tenantId, sessaoId)), RedisValue.Null, ct);
        return Guid.TryParse(valor.ToString(), out var unidadeId) ? unidadeId : null;
    }

    /// <summary>Falso quando o Valkey não respondeu: a troca de unidade não aconteceu.</summary>
    public Task<bool> DefinirUnidadeAsync(string tenantId, Guid sessaoId, Guid unidadeId, CancellationToken ct) =>
        TentarAsync(sessaoId, db => db.StringSetAsync(Chave(tenantId, sessaoId), unidadeId.ToString(), Validade), false, ct);

    public Task<bool> RenovarAsync(string tenantId, Guid sessaoId, CancellationToken ct) =>
        TentarAsync(sessaoId, db => db.KeyExpireAsync(Chave(tenantId, sessaoId), Validade), false, ct);

    public Task<bool> RemoverAsync(string tenantId, Guid sessaoId, CancellationToken ct) =>
        TentarAsync(sessaoId, db => db.KeyDeleteAsync(Chave(tenantId, sessaoId)), false, ct);

    // Tenant in the key: the Valkey is shared by every tenant of the environment (seção 9).
    private static RedisKey Chave(string tenantId, Guid sessaoId) => $"paf:{tenantId}:sessao:{sessaoId}:unidade";

    private async Task<T> TentarAsync<T>(Guid sessaoId, Func<IDatabase, Task<T>> operacao, T seIndisponivel, CancellationToken ct)
    {
        if (_valkey is not { IsConnected: true } valkey)
        {
            return seIndisponivel;
        }

        try
        {
            return await operacao(valkey.GetDatabase()).WaitAsync(PrazoChamada, ct);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "Valkey indisponível para o estado da sessão {SessaoId}; modo degradado", sessaoId);
            return seIndisponivel;
        }
    }
}
