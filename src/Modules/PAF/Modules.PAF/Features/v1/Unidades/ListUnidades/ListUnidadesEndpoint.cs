using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Unidades;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Unidades.ListUnidades;

public static class ListUnidadesEndpoint
{
    internal static RouteHandlerBuilder MapListUnidadesEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/unidades",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ListUnidadesQuery(), ct)))
            .WithName("PafListUnidades")
            .WithSummary("List the PAF units of the tenant")
            .RequirePermission(PafPermissions.Unidades.View);
}
