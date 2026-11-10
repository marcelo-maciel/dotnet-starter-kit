using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Prioridades;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Prioridades.CreatePrioridade;

public static class CreatePrioridadeEndpoint
{
    internal static RouteHandlerBuilder MapCreatePrioridadeEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/prioridades",
                async (CreatePrioridadeCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command, ct)))
            .WithName("PafCreatePrioridade")
            .WithSummary("Create a priority in the current unit")
            .RequirePermission(PafPermissions.Prioridades.Create);
}
