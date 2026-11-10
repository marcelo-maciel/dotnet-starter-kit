using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Estacoes.MidiasDaEstacao;

// The list belongs to the station's painel block (RF04), so it is edited and read under the station permissions.
public static class MidiasDaEstacaoEndpoints
{
    internal static void MapMidiasDaEstacaoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/estacoes/{id:guid}/midias",
                async (Guid id, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ObterMidiasDaEstacaoQuery(id), ct)))
            .WithName("PafGetEstacaoMidias")
            .WithSummary("Ordered media list of a painel station")
            .RequirePermission(PafPermissions.Estacoes.View);

        endpoints.MapPut("/estacoes/{id:guid}/midias",
                async (Guid id, SubstituirMidiasDaEstacaoCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command with { EstacaoId = id }, ct)))
            .WithName("PafReplaceEstacaoMidias")
            .WithSummary("Replace the ordered media list of a painel station")
            .RequirePermission(PafPermissions.Estacoes.Update);
    }
}
