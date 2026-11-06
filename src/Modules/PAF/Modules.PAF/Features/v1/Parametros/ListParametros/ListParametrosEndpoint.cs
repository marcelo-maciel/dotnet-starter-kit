using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Parametros;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Parametros.ListParametros;

public static class ListParametrosEndpoint
{
    internal static RouteHandlerBuilder MapListParametrosEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/parametros",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ListParametrosQuery(), ct)))
            .WithName("PafListParametros")
            .WithSummary("List every RF06 parameter with its effective value and origin for the current unit")
            .RequirePermission(PafPermissions.Parametros.View);
}
