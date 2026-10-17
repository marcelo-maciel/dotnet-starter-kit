using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Estacoes.UpdateEstacao;

public static class UpdateEstacaoEndpoint
{
    internal static RouteHandlerBuilder MapUpdateEstacaoEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPut("/estacoes/{id:guid}",
                async (Guid id, UpdateEstacaoCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command with { Id = id }, ct)))
            .WithName("PafUpdateEstacao")
            .WithSummary("Update a station and its queue and priority lists")
            .RequirePermission(PafPermissions.Estacoes.Update);
}
