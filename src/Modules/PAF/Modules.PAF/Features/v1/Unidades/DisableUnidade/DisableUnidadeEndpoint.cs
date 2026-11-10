using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Unidades;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Unidades.DisableUnidade;

public static class DisableUnidadeEndpoint
{
    internal static RouteHandlerBuilder MapDisableUnidadeEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/unidades/{id:guid}/disable",
                async (Guid id, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new DisableUnidadeCommand(id), ct)))
            .WithName("PafDisableUnidade")
            .WithSummary("Deactivate a PAF unit")
            .RequirePermission(PafPermissions.Unidades.Update);
}
