using FSH.Framework.Eventing.Abstractions;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using Microsoft.AspNetCore.SignalR;

namespace FSH.Modules.PAF.TempoReal;

// Own file instead of AvisosPaf.cs: that file belongs to F04 and C04 in wave 1 (PLANO-FATIA-2, seção 2).

/// <summary>
/// To every person connection of the user (<see cref="PafGrupos.Usuario"/>): the session now operates in another
/// unit without the screen asking, so each window re-reads its context (RF22, sessão revivida pela cascata).
/// </summary>
public sealed record UnidadeCorrenteAlteradaPaf(
    Guid Id, DateTime OccurredOnUtc, string? TenantId, string CorrelationId, string Source,
    string UsuarioId, UnidadeCorrenteAlterada Aviso)
    : AvisoPaf(Id, OccurredOnUtc, TenantId, CorrelationId, Source)
{
    public static UnidadeCorrenteAlteradaPaf Criar(string tenantId, string correlationId, string usuarioId, UnidadeCorrenteAlterada aviso)
    {
        ArgumentNullException.ThrowIfNull(aviso);
        return new(aviso.EventoId, DateTime.UtcNow, tenantId, correlationId, Origem, usuarioId, aviso);
    }

    internal override Task EntregarAsync(IHubClients clientes, CancellationToken ct) =>
        clientes.Group(PafGrupos.Usuario(Tenant, UsuarioId)).SendAsync(UnidadeCorrenteAlterada.Metodo, Aviso, ct);
}

/// <summary>
/// Outbox copy of <see cref="UnidadeCorrenteAlteradaPaf"/>. Delivered as is: the screen re-reads its context from the
/// server on arrival, so a late copy carries no state to go stale.
/// </summary>
public sealed class ReenvioUnidadeCorrenteAlteradaHandler(AvisosTempoReal avisos)
    : IIntegrationEventHandler<UnidadeCorrenteAlteradaPaf>
{
    public Task HandleAsync(UnidadeCorrenteAlteradaPaf @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        return avisos.EntregarAsync(@event, ct);
    }
}
