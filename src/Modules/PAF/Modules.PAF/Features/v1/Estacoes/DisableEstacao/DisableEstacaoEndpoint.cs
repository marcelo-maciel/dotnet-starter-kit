using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Estacoes.DisableEstacao;

public static class DisableEstacaoEndpoint
{
    internal static RouteHandlerBuilder MapDisableEstacaoEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/estacoes/{id:guid}/disable",
                async (Guid id, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new DisableEstacaoCommand(id), ct)))
            .WithName("PafDisableEstacao")
            .WithSummary("Deactivate a station")
            .RequirePermission(PafPermissions.Estacoes.Update);
}
