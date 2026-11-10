using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Estacoes.EnableEstacao;

public static class EnableEstacaoEndpoint
{
    internal static RouteHandlerBuilder MapEnableEstacaoEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/estacoes/{id:guid}/enable",
                async (Guid id, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new EnableEstacaoCommand(id), ct)))
            .WithName("PafEnableEstacao")
            .WithSummary("Activate a station")
            .RequirePermission(PafPermissions.Estacoes.Update);
}
