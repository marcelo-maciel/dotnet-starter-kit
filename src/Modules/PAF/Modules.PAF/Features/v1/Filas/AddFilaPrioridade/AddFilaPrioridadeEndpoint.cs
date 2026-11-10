using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Filas;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Filas.AddFilaPrioridade;

public static class AddFilaPrioridadeEndpoint
{
    internal static RouteHandlerBuilder MapAddFilaPrioridadeEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/filas/{filaId:guid}/prioridades",
                async (Guid filaId, AddFilaPrioridadeCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command with { FilaId = filaId }, ct)))
            .WithName("PafAddFilaPrioridade")
            .WithSummary("Add a priority to a queue's grid")
            .RequirePermission(PafPermissions.Filas.Update);
}
