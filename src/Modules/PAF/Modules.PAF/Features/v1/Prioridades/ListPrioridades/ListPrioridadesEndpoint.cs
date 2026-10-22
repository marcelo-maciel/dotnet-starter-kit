using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Prioridades;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Prioridades.ListPrioridades;

public static class ListPrioridadesEndpoint
{
    internal static RouteHandlerBuilder MapListPrioridadesEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/prioridades",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ListPrioridadesQuery(), ct)))
            .WithName("PafListPrioridades")
            .WithSummary("List the priorities of the current unit")
            .RequirePermission(PafPermissions.Prioridades.View);
}
