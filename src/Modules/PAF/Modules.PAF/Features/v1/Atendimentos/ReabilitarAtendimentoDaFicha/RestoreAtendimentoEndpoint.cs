using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ReabilitarAtendimentoDaFicha;

public static class RestoreAtendimentoEndpoint
{
    internal static RouteHandlerBuilder MapRestoreAtendimentoEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/fichas/atendimentos/{atendimentoId:guid}/reabilitar",
                async (Guid atendimentoId, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ReabilitarAtendimentoDaFichaCommand(atendimentoId), ct)))
            .WithName("PafRestoreAtendimento")
            .WithSummary("Bring a cancelled atendimento back to the queue on the same row (RF12)")
            .RequirePermission(PafPermissions.FichasDoDia.Reabilitar);
    }
}
