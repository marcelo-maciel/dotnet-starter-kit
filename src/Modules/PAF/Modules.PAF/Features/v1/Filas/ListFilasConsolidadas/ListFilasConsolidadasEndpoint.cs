using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Filas;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Filas.ListFilasConsolidadas;

public static class ListFilasConsolidadasEndpoint
{
    internal static RouteHandlerBuilder MapListFilasConsolidadasEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/filas/consolidadas",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ListFilasConsolidadasQuery(), ct)))
            .WithName("PafListFilasConsolidadas")
            .WithSummary("List the queues of every unit in the caller's scope")
            .RequirePermission(PafPermissions.Filas.View);
}
