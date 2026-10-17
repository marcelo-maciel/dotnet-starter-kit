using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Filas;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Filas.UpdateFilaPrioridade;

public static class UpdateFilaPrioridadeEndpoint
{
    internal static RouteHandlerBuilder MapUpdateFilaPrioridadeEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPut("/filas/{filaId:guid}/prioridades/{prioridadeId:guid}",
                async (Guid filaId, Guid prioridadeId, UpdateFilaPrioridadeCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command with { FilaId = filaId, PrioridadeId = prioridadeId }, ct)))
            .WithName("PafUpdateFilaPrioridade")
            .WithSummary("Update a line of a queue's priority grid")
            .RequirePermission(PafPermissions.Filas.Update);
}
