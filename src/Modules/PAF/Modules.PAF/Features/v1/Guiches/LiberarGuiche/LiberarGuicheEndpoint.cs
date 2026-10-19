using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Guiches;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Guiches.LiberarGuiche;

public static class LiberarGuicheEndpoint
{
    internal static RouteHandlerBuilder MapReleaseGuicheEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/guiches/{id:guid}/liberar",
                async (Guid id, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(new LiberarGuicheCommand(id), ct);
                    return Results.NoContent();
                })
            .WithName("PafReleaseGuiche")
            .WithSummary("Release a guichê held by a session, refused while an atendimento is in progress on it")
            .RequirePermission(PafPermissions.Guiches.Liberar);
}
