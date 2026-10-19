using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Unidades;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Unidades.GetAdministracaoUnidades;

public static class GetAdministracaoUnidadesEndpoint
{
    internal static RouteHandlerBuilder MapGetAdministracaoUnidadesEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/unidades/administracao",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new GetAdministracaoUnidadesQuery(), ct)))
            .WithName("PafGetAdministracaoUnidades")
            .WithSummary("Whether the caller administers the PAF units of the tenant")
            .RequirePermission(PafPermissions.Unidades.View);
}
