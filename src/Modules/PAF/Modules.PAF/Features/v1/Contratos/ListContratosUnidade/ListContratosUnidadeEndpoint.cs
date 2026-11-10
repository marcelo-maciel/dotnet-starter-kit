using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Contratos;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Contratos.ListContratosUnidade;

public static class ListContratosUnidadeEndpoint
{
    internal static RouteHandlerBuilder MapListContratosUnidadeEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/contratos",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ListContratosUnidadeQuery(), ct)))
            .WithName("PafListContratosUnidade")
            .WithSummary("List the contracts of the current unit")
            .RequirePermission(PafPermissions.Contratos.View);
}
