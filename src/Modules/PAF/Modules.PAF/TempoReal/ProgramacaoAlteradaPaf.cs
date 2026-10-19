using FSH.Framework.Eventing.Abstractions;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using Microsoft.AspNetCore.SignalR;

namespace FSH.Modules.PAF.TempoReal;

// Own file instead of AvisosPaf.cs, like UnidadeCorrenteAlteradaPaf: the outbox stores this type by name, so it stays here.

/// <summary>
/// To every connection of the unidade (M06): a mídia, a programação or a painel list of the unidade changed, so each
/// painel re-reads what it plays. Written by every mídia (M03), programação (M05) and painel list (M06) write.
/// </summary>
public sealed record ProgramacaoAlteradaPaf(
    Guid Id, DateTime OccurredOnUtc, string? TenantId, string CorrelationId, string Source,
    ProgramacaoAlterada Aviso)
    : AvisoPaf(Id, OccurredOnUtc, TenantId, CorrelationId, Source)
{
    public static ProgramacaoAlteradaPaf Criar(string tenantId, Guid unidadeId)
    {
        var aviso = new ProgramacaoAlterada(Guid.CreateVersion7(), unidadeId);
        return new(aviso.EventoId, DateTime.UtcNow, tenantId, OperacaoGuiche.Correlacao(), Origem, aviso);
    }

    internal override Task EntregarAsync(IHubClients clientes, CancellationToken ct) =>
        clientes.Group(PafGrupos.Unidade(Tenant, Aviso.UnidadeId)).SendAsync(ProgramacaoAlterada.Metodo, Aviso, ct);
}

/// <summary>
/// Outbox copy of <see cref="ProgramacaoAlteradaPaf"/>. Delivered as is: the painel re-reads from the server on arrival,
/// so a late copy carries no state to go stale.
/// </summary>
public sealed class ReenvioProgramacaoAlteradaHandler(AvisosTempoReal avisos)
    : IIntegrationEventHandler<ProgramacaoAlteradaPaf>
{
    public Task HandleAsync(ProgramacaoAlteradaPaf @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        return avisos.EntregarAsync(@event, ct);
    }
}
