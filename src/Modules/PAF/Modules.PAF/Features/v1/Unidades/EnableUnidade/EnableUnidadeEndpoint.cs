using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Unidades;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Unidades.EnableUnidade;

public static class EnableUnidadeEndpoint
{
    internal static RouteHandlerBuilder MapEnableUnidadeEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/unidades/{id:guid}/enable",
                async (Guid id, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new EnableUnidadeCommand(id), ct)))
            .WithName("PafEnableUnidade")
            .WithSummary("Activate a PAF unit")
            .RequirePermission(PafPermissions.Unidades.Update);
}
