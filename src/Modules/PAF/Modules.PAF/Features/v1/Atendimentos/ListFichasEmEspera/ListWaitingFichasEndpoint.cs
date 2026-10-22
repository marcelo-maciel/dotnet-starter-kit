using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ListFichasEmEspera;

public static class ListWaitingFichasEndpoint
{
    internal static RouteHandlerBuilder MapListWaitingFichasEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/fichas-em-espera",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ListFichasEmEsperaQuery(), ct)))
            .WithName("PafListWaitingFichas")
            .WithSummary("List what the session guichê could call, in call order (RF04 selecionar ficha)")
            .RequirePermission(PafPermissions.Atendimentos.SelecionarFicha);
}
