using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Unidades;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Unidades.UpdateUnidade;

public static class UpdateUnidadeEndpoint
{
    internal static RouteHandlerBuilder MapUpdateUnidadeEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPut("/unidades/{id:guid}",
                async (Guid id, UpdateUnidadeCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command with { Id = id }, ct)))
            .WithName("PafUpdateUnidade")
            .WithSummary("Update a PAF unit")
            .RequirePermission(PafPermissions.Unidades.Update);
}
