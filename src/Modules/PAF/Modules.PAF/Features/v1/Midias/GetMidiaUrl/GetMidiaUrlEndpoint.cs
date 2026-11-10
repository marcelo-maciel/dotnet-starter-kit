using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Midias;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Midias.GetMidiaUrl;

public static class GetMidiaUrlEndpoint
{
    internal static RouteHandlerBuilder MapGetMidiaUrlEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/midias/{id:guid}/url",
                async (Guid id, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new GetMidiaUrlQuery(id), ct)))
            .WithName("PafGetMidiaUrl")
            .WithSummary("Signed read URL of a media of the current unit, for preview")
            .RequirePermission(PafPermissions.Midias.View);
}
