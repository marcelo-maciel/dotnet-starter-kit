using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Setores;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Setores.UpdateSetor;

public static class UpdateSetorEndpoint
{
    internal static RouteHandlerBuilder MapUpdateSetorEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPut("/setores/{id:guid}",
                async (Guid id, UpdateSetorCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command with { Id = id }, ct)))
            .WithName("PafUpdateSetor")
            .WithSummary("Rename a sector of the current unit")
            .RequirePermission(PafPermissions.Filas.Update);
}
