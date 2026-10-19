using System.Collections.Concurrent;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace FSH.Modules.PAF.TempoReal;

/// <summary>
/// Connections of <see cref="PafHub"/> (seção 7): the local list of this instance, kept for <c>Abort</c> (only the
/// holding instance can abort a connection), and <see cref="RegistroConexoes"/> in Valkey, which reaches the
/// connections of every instance. Group changes go through <see cref="IHubContext{THub}"/>, which crosses the Redis
/// backplane, so a move or a revocation takes a connection out of its groups wherever it is held; the holding instance
/// aborts it, or PafHubFilter refuses its next call. Without Valkey only this instance's connections are reached.
/// </summary>
public sealed partial class ConexoesPaf(IHubContext<PafHub> hub, RegistroConexoes registro, ILogger<ConexoesPaf> logger)
{
    // A group change crosses the Redis backplane; a stalled Valkey must not hold the caller.
    private static readonly TimeSpan LimiteGrupo = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, Local> _locais = new(StringComparer.Ordinal);

    public async Task RegistrarAsync(HubCallerContext contexto, ConexaoPaf conexao, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        ArgumentNullException.ThrowIfNull(conexao);
        _locais[contexto.ConnectionId] = new Local(contexto, conexao);
        await registro.RegistrarAsync(contexto.ConnectionId, conexao, ct).ConfigureAwait(false);
    }

    /// <summary>Disconnect: out of the local list and of the registry, recording the drop of a session's last connection.</summary>
    public async Task RemoverAsync(string connectionId, CancellationToken ct)
    {
        if (!_locais.TryRemove(connectionId, out var local))
        {
            return;
        }

        if (await registro.RemoverAsync(connectionId, local.Conexao, ct).ConfigureAwait(false)
            && local.Conexao is ConexaoPessoa { SessaoId: { } sessaoId })
        {
            LogUltimaConexaoCaiu(logger, local.Conexao.TenantId, sessaoId);
        }
    }

    /// <summary>The connections this instance holds, for the renewal loop.</summary>
    public IReadOnlyCollection<(string ConnectionId, ConexaoPaf Conexao)> Locais() =>
        [.. _locais.Select(par => (par.Key, par.Value.Conexao))];

    /// <summary>Aborts the connection when this instance holds it; another instance's connection is left to its holder.</summary>
    private void AbortarLocal(string connectionId)
    {
        // Abort closes the transport; the disconnect takes the connection out of the local list and the registry.
        if (_locais.TryGetValue(connectionId, out var local))
        {
            local.Contexto.Abort();
        }
    }

    /// <summary>Joins a group and records it, so a revocation on any instance knows which groups to leave.</summary>
    public async Task EntrarNoGrupoAsync(string connectionId, string tenantId, string grupo, CancellationToken ct)
    {
        using var limite = Limite(ct);
        await hub.Groups.AddToGroupAsync(connectionId, grupo, limite.Token).ConfigureAwait(false);
        await LembrarGrupoAsync(connectionId, tenantId, grupo, limite.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts every connection of the session, on any instance, in the group of <paramref name="estacaoId"/> and takes it
    /// out of the previous guichê's group; null only takes it out. Call after the SessaoEstacao change is committed.
    /// </summary>
    public async Task MoverSessaoParaGuicheAsync(string tenantId, Guid sessaoId, Guid? estacaoId, CancellationToken ct)
    {
        var grupo = estacaoId is { } id ? PafGrupos.Guiche(tenantId, id) : null;
        foreach (var connectionId in await DaSessaoAsync(tenantId, sessaoId, ct).ConfigureAwait(false))
        {
            await MoverAsync(tenantId, connectionId, grupo, ct).ConfigureAwait(false);
        }
    }

    internal Task MoverConexaoParaGuicheAsync(string connectionId, string tenantId, Guid estacaoId, CancellationToken ct) =>
        MoverAsync(tenantId, connectionId, PafGrupos.Guiche(tenantId, estacaoId), ct);

    /// <summary>
    /// <see cref="MoverSessaoParaGuicheAsync"/> for the connections of an impersonation grant (S11), keyed by the token
    /// <c>jti</c> as the kit writes it ("N"): the support's guichê group follows the grant's own link, never the user's.
    /// </summary>
    public async Task MoverConcessaoParaGuicheAsync(string tenantId, Guid concessaoId, Guid? estacaoId, CancellationToken ct)
    {
        var concessao = concessaoId.ToString("N");
        var grupo = estacaoId is { } id ? PafGrupos.Guiche(tenantId, id) : null;
        var locais = Filtrar(c => c is ConexaoPessoa p && p.TenantId == tenantId && p.Concessao == concessao);
        foreach (var connectionId in Unir(locais, await registro.DaConcessaoAsync(tenantId, concessao, ct).ConfigureAwait(false)))
        {
            await MoverAsync(tenantId, connectionId, grupo, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Takes every connection of the session, on any instance, out of every PAF group: from now on it receives no
    /// event. Returns how many connections were reached.
    /// </summary>
    public async Task<int> RemoverDeTodosOsGruposAsync(string tenantId, Guid sessaoId, CancellationToken ct)
    {
        var conexoes = await DaSessaoAsync(tenantId, sessaoId, ct).ConfigureAwait(false);
        await RemoverDosGruposAsync(tenantId, conexoes, ct).ConfigureAwait(false);
        return conexoes.Count;
    }

    /// <summary>Same as the session overload, for the connections of an impersonation grant (<c>jti</c>).</summary>
    public async Task<int> RemoverDeTodosOsGruposAsync(string tenantId, string concessao, CancellationToken ct)
    {
        var locais = Filtrar(c => c is ConexaoPessoa p && p.TenantId == tenantId && p.Concessao == concessao);
        var conexoes = Unir(locais, await registro.DaConcessaoAsync(tenantId, concessao, ct).ConfigureAwait(false));
        await RemoverDosGruposAsync(tenantId, conexoes, ct).ConfigureAwait(false);
        return conexoes.Count;
    }

    /// <summary>
    /// Closes every connection of the station, any role (link revoked, station deactivated): out of every group on
    /// any instance, aborted where this instance holds it. Call after the revocation is committed: the client
    /// reconnects and the per-request check refuses the dead credential.
    /// </summary>
    public async Task<int> EncerrarEstacaoAsync(string tenantId, Guid estacaoId, CancellationToken ct)
    {
        var locais = Filtrar(c => c is ConexaoEstacao e && e.TenantId == tenantId && e.EstacaoId == estacaoId);
        var emissao = await registro.DaEstacaoAsync(tenantId, estacaoId, PafPapeisEstacao.Emissao, ct).ConfigureAwait(false);
        var painel = await registro.DaEstacaoAsync(tenantId, estacaoId, PafPapeisEstacao.Painel, ct).ConfigureAwait(false);
        var conexoes = Unir(Unir(locais, emissao), painel);
        return await EncerrarAsync(tenantId, conexoes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes every connection of a revoked kit session (single session, logout, revocation), on any instance:
    /// <see cref="SessaoInvalidada"/> with <paramref name="motivo"/> goes first, to those connections only (never the
    /// user group, which may already hold the new session's), then they leave every group and the ones held here are
    /// aborted. A repeat (the kit event after a PAF-born revocation) sends no second notice: the session id is marked
    /// in the registry, and a connection held here is gone by then anyway.
    /// </summary>
    public async Task<int> EncerrarSessaoAsync(string tenantId, Guid sessaoId, string motivo, CancellationToken ct)
    {
        var conexoes = await DaSessaoAsync(tenantId, sessaoId, ct).ConfigureAwait(false);
        if (conexoes.Count > 0 && await registro.PrimeiroAvisoDeInvalidacaoAsync(tenantId, sessaoId, ct).ConfigureAwait(false))
        {
            await AvisarAsync(conexoes, new SessaoInvalidada(sessaoId, motivo), ct).ConfigureAwait(false);
        }

        return await EncerrarAsync(tenantId, conexoes, ct).ConfigureAwait(false);
    }

    // One id at a time, never Clients(list): the Redis lifetime manager writes a single local connection straight to
    // its transport, so the notice is flushed before the abort, while the list overload publishes every id through
    // the backplane, local ones too, and would lose the race with the abort. A remote id still goes over the backplane.
    // The notice is the screen's experience, not the revocation: a failed or stalled send is logged and the close goes on.
    private async Task AvisarAsync(IReadOnlyCollection<string> conexoes, SessaoInvalidada aviso, CancellationToken ct)
    {
        using var limite = Limite(ct);
        foreach (var connectionId in conexoes)
        {
            try
            {
                // The backplane publish ignores the token: the ceiling is applied on the wait.
                await hub.Clients.Client(connectionId).SendAsync(SessaoInvalidada.Metodo, aviso, limite.Token)
                    .WaitAsync(limite.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                LogAvisoNaoEnviado(logger, ex, connectionId, aviso.SessaoId);
            }
        }
    }

    private async Task<int> EncerrarAsync(string tenantId, IReadOnlyCollection<string> conexoes, CancellationToken ct)
    {
        await RemoverDosGruposAsync(tenantId, conexoes, ct).ConfigureAwait(false);
        foreach (var connectionId in conexoes)
        {
            AbortarLocal(connectionId);
        }

        return conexoes.Count;
    }

    private async Task<IReadOnlyCollection<string>> DaSessaoAsync(string tenantId, Guid sessaoId, CancellationToken ct)
    {
        var locais = Filtrar(c => c is ConexaoPessoa p && p.TenantId == tenantId && p.SessaoId == sessaoId);
        return Unir(locais, await registro.DaSessaoAsync(tenantId, sessaoId, ct).ConfigureAwait(false));
    }

    private List<string> Filtrar(Func<ConexaoPaf, bool> alvo) =>
        [.. _locais.Where(par => alvo(par.Value.Conexao)).Select(par => par.Key)];

    // Registry null = Valkey did not answer: only this instance's connections are reachable (modo degradado).
    private static HashSet<string> Unir(IEnumerable<string> locais, IReadOnlyList<string>? doRegistro)
    {
        var todas = new HashSet<string>(locais, StringComparer.Ordinal);
        if (doRegistro is not null)
        {
            todas.UnionWith(doRegistro);
        }

        return todas;
    }

    private async Task RemoverDosGruposAsync(string tenantId, IReadOnlyCollection<string> conexoes, CancellationToken ct)
    {
        using var limite = Limite(ct);
        foreach (var connectionId in conexoes)
        {
            var grupos = new HashSet<string>(await registro.GruposAsync(tenantId, connectionId, limite.Token).ConfigureAwait(false), StringComparer.Ordinal);
            if (_locais.TryGetValue(connectionId, out var local))
            {
                lock (local)
                {
                    grupos.UnionWith(local.Grupos);
                    local.Grupos.Clear();
                    local.GrupoGuiche = null;
                }
            }

            foreach (var grupo in grupos)
            {
                await hub.Groups.RemoveFromGroupAsync(connectionId, grupo, limite.Token).ConfigureAwait(false);
            }

            await registro.EsquecerGruposAsync(tenantId, connectionId, limite.Token).ConfigureAwait(false);
        }
    }

    private async Task MoverAsync(string tenantId, string connectionId, string? grupo, CancellationToken ct)
    {
        _locais.TryGetValue(connectionId, out var local);
        string? anteriorLocal = null;
        if (local is not null)
        {
            lock (local)
            {
                anteriorLocal = local.GrupoGuiche;
                local.GrupoGuiche = grupo;
            }
        }

        using var limite = Limite(ct);
        var troca = await registro.TrocarGuicheAsync(tenantId, connectionId, grupo, limite.Token).ConfigureAwait(false);
        if (!troca.Disponivel && local is null)
        {
            // Held by another instance and Valkey is down: this instance cannot know its groups.
            return;
        }

        var anterior = troca.Disponivel ? troca.Anterior : anteriorLocal;
        if (anterior is not null && anterior != grupo)
        {
            await hub.Groups.RemoveFromGroupAsync(connectionId, anterior, limite.Token).ConfigureAwait(false);
            await EsquecerGrupoAsync(connectionId, tenantId, anterior, limite.Token).ConfigureAwait(false);
        }

        if (grupo is null)
        {
            return;
        }

        await hub.Groups.AddToGroupAsync(connectionId, grupo, limite.Token).ConfigureAwait(false);
        await LembrarGrupoAsync(connectionId, tenantId, grupo, limite.Token).ConfigureAwait(false);

        // A concurrent move may have swapped the target after this one read it: the newer move owns the
        // connection, so this group comes back out instead of lingering next to the newer one.
        if (await GuicheCorrenteAsync(tenantId, connectionId, local, limite.Token).ConfigureAwait(false) != grupo)
        {
            await hub.Groups.RemoveFromGroupAsync(connectionId, grupo, limite.Token).ConfigureAwait(false);
            await EsquecerGrupoAsync(connectionId, tenantId, grupo, limite.Token).ConfigureAwait(false);
            LogMovimentoSuperado(logger, connectionId, grupo);
        }
    }

    private async Task<string?> GuicheCorrenteAsync(string tenantId, string connectionId, Local? local, CancellationToken ct)
    {
        var corrente = await registro.GuicheAsync(tenantId, connectionId, ct).ConfigureAwait(false);
        if (corrente.Disponivel || local is null)
        {
            return corrente.Anterior;
        }

        lock (local)
        {
            return local.GrupoGuiche;
        }
    }

    private async Task LembrarGrupoAsync(string connectionId, string tenantId, string grupo, CancellationToken ct)
    {
        if (_locais.TryGetValue(connectionId, out var local))
        {
            lock (local)
            {
                local.Grupos.Add(grupo);
            }
        }

        await registro.AdicionarGrupoAsync(tenantId, connectionId, grupo, ct).ConfigureAwait(false);
    }

    private async Task EsquecerGrupoAsync(string connectionId, string tenantId, string grupo, CancellationToken ct)
    {
        if (_locais.TryGetValue(connectionId, out var local))
        {
            lock (local)
            {
                local.Grupos.Remove(grupo);
            }
        }

        await registro.RemoverGrupoAsync(tenantId, connectionId, grupo, ct).ConfigureAwait(false);
    }

    private static CancellationTokenSource Limite(CancellationToken ct)
    {
        var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(LimiteGrupo);
        return limite;
    }

    private sealed class Local(HubCallerContext contexto, ConexaoPaf conexao)
    {
        public HubCallerContext Contexto { get; } = contexto;
        public ConexaoPaf Conexao { get; } = conexao;
        public HashSet<string> Grupos { get; } = new(StringComparer.Ordinal);
        public string? GrupoGuiche { get; set; }
    }

    [LoggerMessage(EventId = 9004, Level = LogLevel.Information,
        Message = "PafHub movimento de guiche superado por outro concorrente {ConnectionId} grupo={Grupo}")]
    private static partial void LogMovimentoSuperado(ILogger logger, string connectionId, string grupo);

    [LoggerMessage(EventId = 9105, Level = LogLevel.Warning,
        Message = "PafHub aviso SessaoInvalidada nao enviado {ConnectionId} sessao={SessaoId}; a conexao e encerrada assim mesmo")]
    private static partial void LogAvisoNaoEnviado(ILogger logger, Exception ex, string connectionId, Guid sessaoId);

    [LoggerMessage(EventId = 9011, Level = LogLevel.Information,
        Message = "PafHub ultima conexao da sessao caiu tenant={TenantId} sessao={SessaoId}; soltura depois da carencia")]
    private static partial void LogUltimaConexaoCaiu(ILogger logger, string tenantId, Guid sessaoId);
}
