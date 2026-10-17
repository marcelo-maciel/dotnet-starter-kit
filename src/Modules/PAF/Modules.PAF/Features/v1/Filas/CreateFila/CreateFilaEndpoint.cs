using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Filas;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Filas.CreateFila;

public static class CreateFilaEndpoint
{
    internal static RouteHandlerBuilder MapCreateFilaEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/filas",
                async (CreateFilaCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command, ct)))
            .WithName("PafCreateFila")
            .WithSummary("Create a queue with its priority grid in the current unit")
            .RequirePermission(PafPermissions.Filas.Create);
}
