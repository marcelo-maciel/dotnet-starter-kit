using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Estacoes.CreateEstacao;

public static class CreateEstacaoEndpoint
{
    internal static RouteHandlerBuilder MapCreateEstacaoEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/estacoes",
                async (CreateEstacaoCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command, ct)))
            .WithName("PafCreateEstacao")
            .WithSummary("Create a station in the current unit")
            .RequirePermission(PafPermissions.Estacoes.Create);
}
