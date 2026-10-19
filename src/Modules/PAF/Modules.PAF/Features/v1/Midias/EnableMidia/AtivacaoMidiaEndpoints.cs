using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Midias;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Midias.EnableMidia;

public static class AtivacaoMidiaEndpoints
{
    internal static RouteHandlerBuilder MapEnableMidiaEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/midias/{id:guid}/enable",
                async (Guid id, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new EnableMidiaCommand(id), ct)))
            .WithName("PafEnableMidia")
            .WithSummary("Activate a media")
            .RequirePermission(PafPermissions.Midias.Update);

    internal static RouteHandlerBuilder MapDisableMidiaEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/midias/{id:guid}/disable",
                async (Guid id, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new DisableMidiaCommand(id), ct)))
            .WithName("PafDisableMidia")
            .WithSummary("Deactivate a media (there is no deletion)")
            .RequirePermission(PafPermissions.Midias.Update);
}
