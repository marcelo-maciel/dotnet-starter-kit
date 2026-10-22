using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Unidades;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Unidades.CreateUnidade;

public static class CreateUnidadeEndpoint
{
    internal static RouteHandlerBuilder MapCreateUnidadeEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/unidades",
                async (CreateUnidadeCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command, ct)))
            .WithName("PafCreateUnidade")
            .WithSummary("Create a PAF unit")
            .RequirePermission(PafPermissions.Unidades.Create);
}
