using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.EncaminharAtendimento;

public static class SendAtendimentoToFilasEndpoint
{
    internal static RouteHandlerBuilder MapSendAtendimentoToFilasEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/atendimentos/{atendimentoId:guid}/encaminhar",
                async (Guid atendimentoId, EncaminharAtendimentoRequest request, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new EncaminharAtendimentoCommand(atendimentoId, request.FilaIds ?? [], request.PrioridadeId), ct)))
            .WithName("PafSendAtendimentoToFilas")
            .WithSummary("Forward the started atendimento to one or more filas, same ficha and senha (RF17)")
            .RequirePermission(PafPermissions.Atendimentos.Encaminhar);
    }
}
