using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Midias;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.ProgramacoesMidia;

public static class SearchProgramacoesMidiaEndpoint
{
    internal static RouteHandlerBuilder MapSearchProgramacoesMidiaEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/programacoes-midia",
                async ([AsParameters] SearchProgramacoesMidiaQuery query, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(query, ct)))
            .WithName("PafSearchProgramacoesMidia")
            .WithSummary("Search the media schedules of the current unit by name, day, hour and period")
            .RequirePermission(PafPermissions.Programacoes.View);
}
