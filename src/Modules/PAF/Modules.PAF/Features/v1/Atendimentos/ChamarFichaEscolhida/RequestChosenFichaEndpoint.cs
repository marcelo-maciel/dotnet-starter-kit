using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ChamarFichaEscolhida;

public static class RequestChosenFichaEndpoint
{
    internal static RouteHandlerBuilder MapRequestChosenFichaEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/atendimentos/{atendimentoId:guid}/chamar",
                async (Guid atendimentoId, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ChamarFichaEscolhidaCommand(atendimentoId), ct)))
            .WithName("PafRequestChosenFicha")
            .WithSummary("Call the chosen waiting atendimento on the session guichê (RF04 selecionar ficha)")
            .RequirePermission(PafPermissions.Atendimentos.SelecionarFicha);
}
