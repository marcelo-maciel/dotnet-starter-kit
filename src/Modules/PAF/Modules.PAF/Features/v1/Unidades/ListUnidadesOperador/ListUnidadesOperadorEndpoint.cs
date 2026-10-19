using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Unidades;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Unidades.ListUnidadesOperador;

public static class ListUnidadesOperadorEndpoint
{
    internal static RouteHandlerBuilder MapListUnidadesOperadorEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/operador/unidades",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ListUnidadesOperadorQuery(), ct)))
            .WithName("PafListUnidadesOperador")
            .WithSummary("List the units and contracts of the tenant in the header (Codefi operator)")
            .RequirePermission(PafPermissions.Unidades.Operar);
}
