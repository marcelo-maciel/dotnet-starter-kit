using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Filas;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Filas.ListFilas;

public static class ListFilasEndpoint
{
    internal static RouteHandlerBuilder MapListFilasEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/filas",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ListFilasQuery(), ct)))
            .WithName("PafListFilas")
            .WithSummary("List the queues of the current unit")
            .RequirePermission(PafPermissions.Filas.View);
}
