using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Fichas.CancelarAtendimentoDaFicha;

public static class VoidAtendimentoDaFichaEndpoint
{
    internal static RouteHandlerBuilder MapVoidAtendimentoDaFichaEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/fichas/atendimentos/{atendimentoId:guid}/cancelar",
                async (Guid atendimentoId, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new CancelarAtendimentoDaFichaCommand(atendimentoId), ct)))
            .WithName("PafVoidAtendimentoDaFicha")
            .WithSummary("Cancel an atendimento in any non-terminal state, outside the guichê (RF12)")
            .RequirePermission(PafPermissions.FichasDoDia.Cancelar);
}
