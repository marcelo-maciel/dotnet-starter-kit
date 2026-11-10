using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Filas;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Filas.EnableFila;

public static class EnableFilaEndpoint
{
    internal static RouteHandlerBuilder MapEnableFilaEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/filas/{id:guid}/enable",
                async (Guid id, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new EnableFilaCommand(id), ct)))
            .WithName("PafEnableFila")
            .WithSummary("Activate a queue")
            .RequirePermission(PafPermissions.Filas.Update);
}
