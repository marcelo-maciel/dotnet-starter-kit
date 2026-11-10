using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Guiches;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Guiches.ListGuichesDaUnidade;

public static class ListGuichesDaUnidadeEndpoint
{
    internal static RouteHandlerBuilder MapListGuichesDaUnidadeEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/guiches",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ListGuichesDaUnidadeQuery(), ct)))
            .WithName("PafListGuichesDaUnidade")
            .WithSummary("List the active guichês of the current unit and who holds each one")
            .RequirePermission(PafPermissions.Guiches.Selecionar);
}
