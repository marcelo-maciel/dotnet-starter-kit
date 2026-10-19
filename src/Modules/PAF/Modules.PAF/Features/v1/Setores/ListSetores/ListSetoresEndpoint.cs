using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Setores;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Setores.ListSetores;

public static class ListSetoresEndpoint
{
    internal static RouteHandlerBuilder MapListSetoresEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/setores",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ListSetoresQuery(), ct)))
            .WithName("PafListSetores")
            .WithSummary("List the sectors of the current unit")
            .RequirePermission(PafPermissions.Filas.View);
}
