using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Prioridades;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Prioridades.UpdatePrioridade;

public static class UpdatePrioridadeEndpoint
{
    internal static RouteHandlerBuilder MapUpdatePrioridadeEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPut("/prioridades/{id:guid}",
                async (Guid id, UpdatePrioridadeCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command with { Id = id }, ct)))
            .WithName("PafUpdatePrioridade")
            .WithSummary("Update a priority")
            .RequirePermission(PafPermissions.Prioridades.Update);
}
