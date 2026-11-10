using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Midias;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Midias.UpdateMidia;

public static class UpdateMidiaEndpoint
{
    internal static RouteHandlerBuilder MapUpdateMidiaEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPut("/midias/{id:guid}",
                async (Guid id, UpdateMidiaCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command with { Id = id }, ct)))
            .WithName("PafUpdateMidia")
            .WithSummary("Edit description and display duration of a media")
            .RequirePermission(PafPermissions.Midias.Update);
}
