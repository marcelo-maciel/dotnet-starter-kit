using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.TempoReal;

/// <summary>
/// Registry of the live <see cref="PafHub"/> connections (seção 7), kept by the hub's connect and disconnect
/// events. It answers which connections belong to a session or to a station, so the guichê group follows the
/// SessaoEstacao link and a revocation closes the connections instead of waiting for the token to expire.
/// </summary>
// ponytail: in-memory, per instance. With a second instance a move or a revocation only reaches the connections the
// running instance holds. Upgrade path before the second instance: the Valkey structure of seção 7 (entries with their
// own expiry, renewed each minute by the holding instance), RemoveFromGroupAsync across the backplane, and the abort
// left to the holding instance through PafHubFilter.
public sealed partial class ConexoesPaf(IHubContext<PafHub> hub, ILogger<ConexoesPaf> logger)
{
    // A group change crosses the Redis backplane; a stalled Valkey must not hold the caller.
    private static readonly TimeSpan LimiteGrupo = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, Registro> _conexoes = new(StringComparer.Ordinal);

    internal void Registrar(HubCallerContext contexto, ConexaoPaf conexao) =>
        _conexoes[contexto.ConnectionId] = new Registro(contexto, conexao);

    internal void Remover(string connectionId) => _conexoes.TryRemove(connectionId, out _);

    /// <summary>
    /// Puts every connection of the session in the group of <paramref name="estacaoId"/> and takes it out of the
    /// previous guichê's group; null only takes it out. Call after the SessaoEstacao change is committed.
    /// </summary>
    public async Task MoverSessaoParaGuicheAsync(string tenantId, Guid sessaoId, Guid? estacaoId, CancellationToken ct)
    {
        var grupo = estacaoId is { } id ? PafGrupos.Guiche(tenantId, id) : null;
        foreach (var registro in _conexoes.Values.Where(r => r.Conexao is ConexaoPessoa p && p.TenantId == tenantId && p.SessaoId == sessaoId))
        {
            await MoverAsync(registro, grupo, ct).ConfigureAwait(false);
        }
    }

    internal Task MoverConexaoParaGuicheAsync(string connectionId, string tenantId, Guid estacaoId, CancellationToken ct) =>
        _conexoes.TryGetValue(connectionId, out var registro)
            ? MoverAsync(registro, PafGrupos.Guiche(tenantId, estacaoId), ct)
            : Task.CompletedTask;

    /// <summary>
    /// Closes every connection of the station, any role (link revoked, station deactivated). Call after the
    /// revocation is committed: the client reconnects and the per-request check refuses the dead credential.
    /// </summary>
    public int EncerrarEstacao(string tenantId, Guid estacaoId) =>
        Encerrar(r => r.Conexao is ConexaoEstacao e && e.TenantId == tenantId && e.EstacaoId == estacaoId);

    /// <summary>Closes every connection of a revoked kit session (single session, logout).</summary>
    public int EncerrarSessao(string tenantId, Guid sessaoId) =>
        Encerrar(r => r.Conexao is ConexaoPessoa p && p.TenantId == tenantId && p.SessaoId == sessaoId);

    private int Encerrar(Func<Registro, bool> alvo)
    {
        var encerradas = 0;
        foreach (var registro in _conexoes.Values.Where(alvo))
        {
            // Abort closes the transport; the disconnect drops the connection from every group and from here.
            registro.Contexto.Abort();
            encerradas++;
        }

        return encerradas;
    }

    private async Task MoverAsync(Registro registro, string? grupo, CancellationToken ct)
    {
        string? anterior;
        lock (registro)
        {
            anterior = registro.GrupoGuiche;
            registro.GrupoGuiche = grupo;
        }

        var connectionId = registro.Contexto.ConnectionId;
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(LimiteGrupo);
        if (anterior is not null && anterior != grupo)
        {
            await hub.Groups.RemoveFromGroupAsync(connectionId, anterior, limite.Token).ConfigureAwait(false);
        }

        if (grupo is null)
        {
            return;
        }

        await hub.Groups.AddToGroupAsync(connectionId, grupo, limite.Token).ConfigureAwait(false);

        // A concurrent move may have swapped the target after this one read it: the newer move owns the
        // connection, so this group comes back out instead of lingering next to the newer one.
        bool superado;
        lock (registro)
        {
            superado = registro.GrupoGuiche != grupo;
        }

        if (superado)
        {
            await hub.Groups.RemoveFromGroupAsync(connectionId, grupo, limite.Token).ConfigureAwait(false);
            LogMovimentoSuperado(logger, connectionId, grupo);
        }
    }

    private sealed class Registro(HubCallerContext contexto, ConexaoPaf conexao)
    {
        public HubCallerContext Contexto { get; } = contexto;
        public ConexaoPaf Conexao { get; } = conexao;
        public string? GrupoGuiche { get; set; }
    }

    [LoggerMessage(EventId = 9004, Level = LogLevel.Information,
        Message = "PafHub movimento de guiche superado por outro concorrente {ConnectionId} grupo={Grupo}")]
    private static partial void LogMovimentoSuperado(ILogger logger, string connectionId, string grupo);
}
