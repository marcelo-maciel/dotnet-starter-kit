using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Prioridades;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Prioridades.EnablePrioridade;

public static class EnablePrioridadeEndpoint
{
    internal static RouteHandlerBuilder MapEnablePrioridadeEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/prioridades/{id:guid}/enable",
                async (Guid id, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new EnablePrioridadeCommand(id), ct)))
            .WithName("PafEnablePrioridade")
            .WithSummary("Activate a priority")
            .RequirePermission(PafPermissions.Prioridades.Update);
}
