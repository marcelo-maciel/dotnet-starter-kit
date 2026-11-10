using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Prioridades;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Prioridades.DisablePrioridade;

public static class DisablePrioridadeEndpoint
{
    internal static RouteHandlerBuilder MapDisablePrioridadeEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/prioridades/{id:guid}/disable",
                async (Guid id, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new DisablePrioridadeCommand(id), ct)))
            .WithName("PafDisablePrioridade")
            .WithSummary("Deactivate a priority")
            .RequirePermission(PafPermissions.Prioridades.Update);
}
