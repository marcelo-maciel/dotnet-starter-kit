using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Filas;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Filas.DisableFila;

public static class DisableFilaEndpoint
{
    internal static RouteHandlerBuilder MapDisableFilaEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/filas/{id:guid}/disable",
                async (Guid id, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new DisableFilaCommand(id), ct)))
            .WithName("PafDisableFila")
            .WithSummary("Deactivate a queue")
            .RequirePermission(PafPermissions.Filas.Update);
}
