using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Estacoes.RevogarVinculo;

public static class RevokeStationLinkEndpoint
{
    internal static RouteHandlerBuilder MapRevokeStationLinkEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/paf/estacoes/{estacaoId:guid}/vinculo/revogar",
                async (Guid estacaoId, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(new RevogarVinculoEstacaoCommand(estacaoId), ct);
                    return Results.NoContent();
                })
            .WithName("RevokeStationLink")
            .WithSummary("Revoke the station's current link and credentials without deleting the station")
            .RequirePermission(PafPermissions.VinculosEstacao.Revoke);
    }
}
