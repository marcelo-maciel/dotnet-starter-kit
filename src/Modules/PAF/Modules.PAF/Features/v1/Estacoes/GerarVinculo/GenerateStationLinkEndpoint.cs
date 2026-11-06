using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Estacoes.GerarVinculo;

public static class GenerateStationLinkEndpoint
{
    internal static RouteHandlerBuilder MapGenerateStationLinkEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/paf/estacoes/{estacaoId:guid}/vinculo",
                async (Guid estacaoId, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new GerarVinculoEstacaoCommand(estacaoId), ct)))
            .WithName("GenerateStationLink")
            .WithSummary("Generate the one-time link of an emission or panel station, revoking the previous one")
            .RequirePermission(PafPermissions.VinculosEstacao.Generate);
    }
}
