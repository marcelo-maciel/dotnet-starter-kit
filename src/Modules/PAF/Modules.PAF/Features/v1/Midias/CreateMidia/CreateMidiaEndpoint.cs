using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Midias;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Midias.CreateMidia;

public static class CreateMidiaEndpoint
{
    internal static RouteHandlerBuilder MapCreateMidiaEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/midias",
                async (CreateMidiaCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command, ct)))
            .WithName("PafCreateMidia")
            .WithSummary("Confirm an upload and create the media of the current unit")
            .RequirePermission(PafPermissions.Midias.Create);
}
