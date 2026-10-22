using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Guiches;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Guiches.ObterPosto;

public static class GetPostoGuicheEndpoint
{
    internal static RouteHandlerBuilder MapGetPostoGuicheEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/posto",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ObterPostoGuicheQuery(), ct)))
            .WithName("PafGetPostoGuiche")
            .WithSummary("Read the session's current unit, selected guichê and atendimento in progress")
            .RequirePermission(PafPermissions.Guiches.Selecionar);
}
