using System.Security.Claims;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Painel;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Painel.ObterEstadoPainel;

// The station id comes from the painel credential, never from the route: a painel reads only its own filas.
public static class GetPanelStateEndpoint
{
    internal static RouteHandlerBuilder MapGetPanelStateEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/paf/estacao/painel/estado",
                async (ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ObterEstadoPainelQuery(Guid.Parse(user.FindFirstValue(PafClaims.Estacao)!)), ct)))
            .WithName("GetPanelState")
            .WithSummary("Latest calls of the painel's filas, re-queried on connect and reconnect")
            .RequireAuthorization(PafPolicies.EstacaoPainel);
    }
}
