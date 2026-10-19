using System.Security.Claims;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Painel;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Painel.ObterMidiasPainel;

// The station id comes from the painel credential, never from the route: a painel reads only its own list.
public static class GetPanelMediaEndpoint
{
    internal static RouteHandlerBuilder MapGetPanelMediaEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/paf/estacao/painel/midias",
                async (ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ObterMidiasPainelQuery(Guid.Parse(user.FindFirstValue(PafClaims.Estacao)!)), ct)))
            .WithName("GetPanelMedia")
            .WithSummary("Media loop and today's programações of the painel, each with a signed read URL")
            .RequireAuthorization(PafPolicies.EstacaoPainel);
    }
}
