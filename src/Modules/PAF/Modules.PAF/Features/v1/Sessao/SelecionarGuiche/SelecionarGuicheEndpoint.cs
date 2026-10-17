using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Sessao;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Sessao.SelecionarGuiche;

public static class SelecionarGuicheEndpoint
{
    internal static RouteHandlerBuilder MapSelecionarGuicheEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/guiche",
                async (SelecionarGuicheCommand command, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(command, ct);
                    return Results.NoContent();
                })
            .WithName("PafSelecionarGuiche")
            .WithSummary("Seleciona o guichê da sessão na unidade corrente")
            .RequirePermission(PafSessaoPermissions.Guiches.Selecionar);
    }
}
