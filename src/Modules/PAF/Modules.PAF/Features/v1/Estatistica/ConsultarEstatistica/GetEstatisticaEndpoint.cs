using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estatistica;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Estatistica.ConsultarEstatistica;

public static class GetEstatisticaEndpoint
{
    internal static RouteHandlerBuilder MapGetEstatisticaEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/estatistica",
                async ([AsParameters] ConsultarEstatisticaQuery query, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(query, ct)))
            .WithName("PafConsultarEstatistica")
            .WithSummary("Statistics of RF07 over the caller's unidade scope")
            .RequirePermission(PafPermissions.Estatisticas.View)
            .Produces<ResultadoEstatisticaDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);
}
