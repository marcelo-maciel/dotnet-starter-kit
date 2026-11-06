using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Midias;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Midias.ListMidias;

public static class ListMidiasEndpoint
{
    internal static RouteHandlerBuilder MapListMidiasEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/midias",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ListMidiasQuery(), ct)))
            .WithName("PafListMidias")
            .WithSummary("List the media of the current unit")
            .RequirePermission(PafPermissions.Midias.View);
}
