using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Estacoes.ListEstacoes;

public static class ListEstacoesEndpoint
{
    internal static RouteHandlerBuilder MapListEstacoesEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/estacoes",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ListEstacoesQuery(), ct)))
            .WithName("PafListEstacoes")
            .WithSummary("List the stations of the current unit")
            .RequirePermission(PafPermissions.Estacoes.View);
}
