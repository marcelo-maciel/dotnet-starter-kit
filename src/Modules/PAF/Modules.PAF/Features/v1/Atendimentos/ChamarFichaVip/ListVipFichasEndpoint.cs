using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ChamarFichaVip;

public static class ListVipFichasEndpoint
{
    internal static RouteHandlerBuilder MapListVipFichasEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/vip/fichas",
                async ([AsParameters] ListFichasVipQuery query, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(query, ct)))
            .WithName("PafListVipFichas")
            .WithSummary("Every ficha of the current unit printed with the senha on the date, for the VIP pick (RF19)")
            .RequirePermission(PafPermissions.Atendimentos.Vip)
            .Produces<IReadOnlyList<FichaVipDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);
}
