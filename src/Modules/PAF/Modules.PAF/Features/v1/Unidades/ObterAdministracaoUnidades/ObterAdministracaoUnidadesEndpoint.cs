using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Unidades;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Unidades.ObterAdministracaoUnidades;

public static class ObterAdministracaoUnidadesEndpoint
{
    internal static RouteHandlerBuilder MapObterAdministracaoUnidadesEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/unidades/administracao",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ObterAdministracaoUnidadesQuery(), ct)))
            .WithName("PafObterAdministracaoUnidades")
            .WithSummary("Whether the caller administers the PAF units of the tenant")
            .RequirePermission(PafPermissions.Unidades.View);
}
