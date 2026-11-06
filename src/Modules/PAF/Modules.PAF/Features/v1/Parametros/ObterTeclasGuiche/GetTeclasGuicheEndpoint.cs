using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Parametros;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Parametros.ObterTeclasGuiche;

public static class GetTeclasGuicheEndpoint
{
    internal static RouteHandlerBuilder MapGetTeclasGuicheEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/teclas-atalho",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ObterTeclasGuicheQuery(), ct)))
            .WithName("PafGetTeclasGuiche")
            .WithSummary("Read the guichê hotkeys of the current unit")
            .RequirePermission(PafPermissions.Guiches.Selecionar);
}
