using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Filas;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Filas.UpdateFila;

public static class UpdateFilaEndpoint
{
    internal static RouteHandlerBuilder MapUpdateFilaEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPut("/filas/{id:guid}",
                async (Guid id, UpdateFilaCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command with { Id = id }, ct)))
            .WithName("PafUpdateFila")
            .WithSummary("Update a queue")
            .RequirePermission(PafPermissions.Filas.Update);
}
