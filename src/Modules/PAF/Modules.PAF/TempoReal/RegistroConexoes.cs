using System.Globalization;
using FSH.Modules.PAF.Sessao;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace FSH.Modules.PAF.TempoReal;

/// <summary>
/// Registro de conexões no Valkey (seção 7, decidido em 03/10/2026): quais conexões vivas cada sessão, concessão de
/// impersonação e estação (por papel) tem, em qualquer instância. Cada índice é um ZSET cujo score é o vencimento da
/// entrada, renovado pela instância que segura a conexão; a instância que cai para de renovar e as entradas dela
/// vencem sozinhas. A queda da última conexão de uma sessão grava o instante em <see cref="ChaveQuedas"/>, que o laço
/// de renovação de qualquer instância consome depois da carência.
/// Sem Valkey registrado, desconectado ou lento, toda operação responde como indisponível dentro do prazo curto e o
/// <see cref="ConexoesPaf"/> segue só com a lista local (modo degradado).
/// </summary>
public sealed partial class RegistroConexoes(
    IEnumerable<IConnectionMultiplexer> multiplexers,
    IOptions<OpcoesSessaoPaf> opcoes,
    TimeProvider relogio,
    ILogger<RegistroConexoes> logger)
{
    /// <summary>Quedas de última conexão de todos os tenants; membro <c>{tenant}|{sessao}</c>, score = instante.</summary>
    public const string ChaveQuedas = "paf:sessoes-caidas";

    // ponytail: fixed per-call ceiling, the one EstadoSessao uses; tune once production Valkey latency is measured.
    private static readonly TimeSpan PrazoChamada = TimeSpan.FromMilliseconds(500);

    // ponytail: one hour covers the kit event's delay (dispatcher interval and retries); a repeat later than that
    // notifies again, harmless once the screen has left. Raise it if the outbox retries ever span longer.
    private static readonly TimeSpan ValidadeAvisoInvalidacao = TimeSpan.FromHours(1);

    // Removes the connection from its indexes and, when it was the session's last live one, records the drop: in one
    // script, so two last connections dropping together on two instances both see the right count.
    private static readonly LuaScript ScriptRemover = LuaScript.Prepare("""
        for i = 1, #KEYS - 2 do redis.call('ZREM', KEYS[i], ARGV[1]) end
        redis.call('DEL', KEYS[#KEYS - 1], KEYS[#KEYS])
        if ARGV[4] ~= '' then
          if redis.call('ZCOUNT', ARGV[4], '(' .. ARGV[2], '+inf') == 0 then
            redis.call('ZADD', ARGV[5], ARGV[2], ARGV[3])
            return 1
          end
        end
        return 0
        """);

    // Swaps the connection's guichê group and returns the previous one; empty new value clears it.
    private static readonly LuaScript ScriptTrocarGuiche = LuaScript.Prepare("""
        local anterior = redis.call('GET', @chave)
        if @grupo == '' then redis.call('DEL', @chave) else redis.call('SET', @chave, @grupo, 'PX', @validade) end
        if anterior then return anterior end
        return ''
        """);

    private readonly IConnectionMultiplexer? _valkey = multiplexers.FirstOrDefault();

    /// <summary>Falso quando o Valkey não respondeu: a conexão fica só na lista local da instância.</summary>
    public Task<bool> RegistrarAsync(string connectionId, ConexaoPaf conexao, CancellationToken ct) =>
        TentarAsync(async db =>
        {
            var agora = Agora();
            var vencimento = agora + Validade();
            var transacao = db.CreateTransaction();
            var escritas = new List<Task>();
            foreach (var indice in Indices(conexao))
            {
                escritas.Add(transacao.SortedSetAddAsync(indice, connectionId, vencimento));
                escritas.Add(transacao.KeyExpireAsync(indice, opcoes.Value.ValidadeEntrada));
            }

            // Back within the grace period: the pending release of the session is dropped.
            if (conexao is ConexaoPessoa { SessaoId: { } sessaoId })
            {
                escritas.Add(transacao.SortedSetRemoveAsync(ChaveQuedas, MembroQueda(conexao.TenantId, sessaoId)));
            }

            await transacao.ExecuteAsync().ConfigureAwait(false);
            await Task.WhenAll(escritas).ConfigureAwait(false);
            return true;
        }, false, connectionId, ct);

    /// <summary>
    /// Tira a conexão do registro (desconexão). Verdadeiro quando ela era a última conexão viva da sessão e a queda
    /// foi gravada para a soltura depois da carência.
    /// </summary>
    public Task<bool> RemoverAsync(string connectionId, ConexaoPaf conexao, CancellationToken ct) =>
        TentarAsync(async db =>
        {
            var chaves = Indices(conexao).ToList();
            chaves.Add(ChaveGrupos(conexao.TenantId, connectionId));
            chaves.Add(ChaveGuiche(conexao.TenantId, connectionId));
            var sessao = conexao is ConexaoPessoa { SessaoId: { } sessaoId }
                ? (Indice: ChaveSessao(conexao.TenantId, sessaoId).ToString(), Membro: MembroQueda(conexao.TenantId, sessaoId))
                : (Indice: string.Empty, Membro: string.Empty);
            var resultado = await db.ScriptEvaluateAsync(
                ScriptRemover.ExecutableScript,
                [.. chaves],
                [connectionId, Agora(), sessao.Membro, sessao.Indice, ChaveQuedas]).ConfigureAwait(false);
            return (int)resultado == 1;
        }, false, connectionId, ct);

    /// <summary>Renova o vencimento das conexões que esta instância segura e descarta as vencidas dos mesmos índices.</summary>
    public Task<bool> RenovarAsync(IReadOnlyCollection<(string ConnectionId, ConexaoPaf Conexao)> locais, CancellationToken ct) =>
        TentarAsync(async db =>
        {
            if (locais.Count == 0)
            {
                return true;
            }

            var agora = Agora();
            var vencimento = agora + Validade();
            var lote = db.CreateBatch();
            var escritas = new List<Task>();
            foreach (var (connectionId, conexao) in locais)
            {
                foreach (var indice in Indices(conexao))
                {
                    escritas.Add(lote.SortedSetAddAsync(indice, connectionId, vencimento));
                }

                escritas.Add(lote.KeyExpireAsync(ChaveGrupos(conexao.TenantId, connectionId), opcoes.Value.ValidadeEntrada));
                escritas.Add(lote.KeyExpireAsync(ChaveGuiche(conexao.TenantId, connectionId), opcoes.Value.ValidadeEntrada));
            }

            // After the adds, so an index created by this batch gets its expiry too.
            foreach (var indice in locais.SelectMany(l => Indices(l.Conexao)).Distinct())
            {
                escritas.Add(lote.SortedSetRemoveRangeByScoreAsync(indice, double.NegativeInfinity, agora));
                escritas.Add(lote.KeyExpireAsync(indice, opcoes.Value.ValidadeEntrada));
            }

            lote.Execute();
            await Task.WhenAll(escritas).ConfigureAwait(false);
            return true;
        }, false, "renovacao", ct);

    /// <summary>Conexões vivas da sessão em qualquer instância; nulo quando o Valkey não respondeu.</summary>
    public Task<IReadOnlyList<string>?> DaSessaoAsync(string tenantId, Guid sessaoId, CancellationToken ct) =>
        VivasAsync(ChaveSessao(tenantId, sessaoId), ct);

    /// <summary>Conexões vivas da concessão de impersonação (claim <c>jti</c>); nulo quando o Valkey não respondeu.</summary>
    public Task<IReadOnlyList<string>?> DaConcessaoAsync(string tenantId, string concessao, CancellationToken ct) =>
        VivasAsync(ChaveConcessao(tenantId, concessao), ct);

    /// <summary>Conexões vivas da estação no papel; nulo quando o Valkey não respondeu.</summary>
    public Task<IReadOnlyList<string>?> DaEstacaoAsync(string tenantId, Guid estacaoId, string papel, CancellationToken ct) =>
        VivasAsync(ChaveEstacao(tenantId, estacaoId, papel), ct);

    public Task<bool> AdicionarGrupoAsync(string tenantId, string connectionId, string grupo, CancellationToken ct) =>
        TentarAsync(async db =>
        {
            var chave = ChaveGrupos(tenantId, connectionId);
            await db.SetAddAsync(chave, grupo).ConfigureAwait(false);
            return await db.KeyExpireAsync(chave, opcoes.Value.ValidadeEntrada).ConfigureAwait(false);
        }, false, connectionId, ct);

    public Task<bool> RemoverGrupoAsync(string tenantId, string connectionId, string grupo, CancellationToken ct) =>
        TentarAsync(db => db.SetRemoveAsync(ChaveGrupos(tenantId, connectionId), grupo), false, connectionId, ct);

    /// <summary>Grupos em que a conexão foi posta, por qualquer instância; vazio quando o Valkey não respondeu.</summary>
    public Task<string[]> GruposAsync(string tenantId, string connectionId, CancellationToken ct) =>
        TentarAsync(
            async db => (await db.SetMembersAsync(ChaveGrupos(tenantId, connectionId)).ConfigureAwait(false))
                .Select(g => g.ToString()).ToArray(),
            [], connectionId, ct);

    public Task<bool> EsquecerGruposAsync(string tenantId, string connectionId, CancellationToken ct) =>
        TentarAsync(
            async db =>
            {
                await db.KeyDeleteAsync([ChaveGrupos(tenantId, connectionId), ChaveGuiche(tenantId, connectionId)]).ConfigureAwait(false);
                return true;
            },
            false, connectionId, ct);

    /// <summary>
    /// Troca o grupo de guichê da conexão e devolve o anterior (<see cref="TrocaGuiche.Anterior"/> nulo quando não
    /// havia). <see cref="TrocaGuiche.Disponivel"/> falso quando o Valkey não respondeu.
    /// </summary>
    public Task<TrocaGuiche> TrocarGuicheAsync(string tenantId, string connectionId, string? grupo, CancellationToken ct) =>
        TentarAsync(
            async db =>
            {
                var anterior = (string?)await db.ScriptEvaluateAsync(ScriptTrocarGuiche, new
                {
                    chave = ChaveGuiche(tenantId, connectionId),
                    grupo = grupo ?? string.Empty,
                    validade = (long)opcoes.Value.ValidadeEntrada.TotalMilliseconds,
                }).ConfigureAwait(false);
                return new TrocaGuiche(true, string.IsNullOrEmpty(anterior) ? null : anterior);
            },
            new TrocaGuiche(false, null), connectionId, ct);

    /// <summary>Grupo de guichê corrente da conexão; <see cref="TrocaGuiche.Disponivel"/> falso sem Valkey.</summary>
    public Task<TrocaGuiche> GuicheAsync(string tenantId, string connectionId, CancellationToken ct) =>
        TentarAsync(
            async db =>
            {
                var valor = await db.StringGetAsync(ChaveGuiche(tenantId, connectionId)).ConfigureAwait(false);
                return new TrocaGuiche(true, valor.IsNullOrEmpty ? null : valor.ToString());
            },
            new TrocaGuiche(false, null), connectionId, ct);

    /// <summary>Quedas de última conexão mais antigas que a carência; vazio quando o Valkey não respondeu.</summary>
    public Task<IReadOnlyList<QuedaSessao>> QuedasVencidasAsync(CancellationToken ct) =>
        TentarAsync<IReadOnlyList<QuedaSessao>>(
            async db =>
            {
                var limite = Agora() - opcoes.Value.CarenciaReconexao.TotalMilliseconds;
                var membros = await db.SortedSetRangeByScoreAsync(ChaveQuedas, double.NegativeInfinity, limite).ConfigureAwait(false);
                return [.. membros.Select(m => LerQueda(m.ToString())).OfType<QuedaSessao>()];
            },
            [], "quedas", ct);

    /// <summary>
    /// Toma a queda para esta instância: só uma das instâncias que a acharam vencida recebe verdadeiro e solta a sessão.
    /// </summary>
    public Task<bool> ReivindicarQuedaAsync(QuedaSessao queda, CancellationToken ct) =>
        TentarAsync(db => db.SortedSetRemoveAsync(ChaveQuedas, MembroQueda(queda.TenantId, queda.SessaoId)), false, "quedas", ct);

    /// <summary>Devolve a queda reivindicada cuja soltura falhou, para o próximo ciclo tentar de novo.</summary>
    public Task<bool> DevolverQuedaAsync(QuedaSessao queda, CancellationToken ct) =>
        TentarAsync(db => db.SortedSetAddAsync(ChaveQuedas, MembroQueda(queda.TenantId, queda.SessaoId), Agora()), false, "quedas", ct);

    /// <summary>
    /// Marca o aviso <c>SessaoInvalidada</c> da sessão, uma vez para todas as instâncias: falso quando ele já saiu (o
    /// evento do kit depois da revogação nascida no PAF, uma reentrega). Sem Valkey conta como primeiro: aí só as
    /// conexões desta instância são alcançadas, e elas são abortadas.
    /// </summary>
    public Task<bool> PrimeiroAvisoDeInvalidacaoAsync(string tenantId, Guid sessaoId, CancellationToken ct) =>
        TentarAsync(
            db => db.StringSetAsync(Chave(tenantId, $"sessao:{sessaoId:D}:invalidada"), "1", ValidadeAvisoInvalidacao, When.NotExists),
            true, "invalidada", ct);

    private async Task<IReadOnlyList<string>?> VivasAsync(RedisKey indice, CancellationToken ct) =>
        await TentarAsync<IReadOnlyList<string>?>(
            async db => [.. (await db.SortedSetRangeByScoreAsync(indice, Agora(), double.PositiveInfinity, Exclude.Start).ConfigureAwait(false))
                .Select(m => m.ToString())],
            null, indice.ToString(), ct).ConfigureAwait(false);

    private double Agora() => relogio.GetUtcNow().ToUnixTimeMilliseconds();

    private double Validade() => opcoes.Value.ValidadeEntrada.TotalMilliseconds;

    private static IEnumerable<RedisKey> Indices(ConexaoPaf conexao)
    {
        switch (conexao)
        {
            case ConexaoEstacao e:
                yield return ChaveEstacao(e.TenantId, e.EstacaoId, e.Papel);
                break;
            case ConexaoPessoa p:
                if (p.SessaoId is { } sessaoId)
                {
                    yield return ChaveSessao(p.TenantId, sessaoId);
                }

                if (p.Concessao is { Length: > 0 } concessao)
                {
                    yield return ChaveConcessao(p.TenantId, concessao);
                }

                break;
        }
    }

    // Tenant in every key: ids are not unique across tenants and the Valkey is shared by the environment (seção 9).
    private static RedisKey ChaveSessao(string tenantId, Guid sessaoId) => Chave(tenantId, $"conexoes:sessao:{sessaoId:D}");

    private static RedisKey ChaveConcessao(string tenantId, string concessao) => Chave(tenantId, $"conexoes:concessao:{concessao}");

    private static RedisKey ChaveEstacao(string tenantId, Guid estacaoId, string papel) =>
        Chave(tenantId, $"conexoes:estacao:{estacaoId:D}:{papel}");

    private static RedisKey ChaveGrupos(string tenantId, string connectionId) => Chave(tenantId, $"conexao:{connectionId}:grupos");

    private static RedisKey ChaveGuiche(string tenantId, string connectionId) => Chave(tenantId, $"conexao:{connectionId}:guiche");

    private static RedisKey Chave(string tenantId, string resto) => string.Create(CultureInfo.InvariantCulture, $"paf:{tenantId}:{resto}");

    private static string MembroQueda(string tenantId, Guid sessaoId) => $"{tenantId}|{sessaoId:D}";

    private static QuedaSessao? LerQueda(string membro)
    {
        var separador = membro.LastIndexOf('|');
        return separador > 0 && Guid.TryParse(membro[(separador + 1)..], out var sessaoId)
            ? new QuedaSessao(membro[..separador], sessaoId)
            : null;
    }

    private async Task<T> TentarAsync<T>(Func<IDatabase, Task<T>> operacao, T seIndisponivel, string alvo, CancellationToken ct)
    {
        if (_valkey is not { IsConnected: true } valkey)
        {
            return seIndisponivel;
        }

        try
        {
            // StackExchange.Redis ignores cancellation on commands: the ceiling is applied on the wait.
            return await operacao(valkey.GetDatabase()).WaitAsync(PrazoChamada, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogValkeyIndisponivel(logger, ex, alvo);
            return seIndisponivel;
        }
    }

    [LoggerMessage(EventId = 9010, Level = LogLevel.Warning,
        Message = "Valkey indisponivel para o registro de conexoes ({Alvo}); modo degradado, so a lista local")]
    private static partial void LogValkeyIndisponivel(ILogger logger, Exception ex, string alvo);
}

/// <summary>Sessão cuja última conexão caiu; o laço de renovação a solta depois da carência.</summary>
public sealed record QuedaSessao(string TenantId, Guid SessaoId);

public readonly record struct TrocaGuiche(bool Disponivel, string? Anterior);
