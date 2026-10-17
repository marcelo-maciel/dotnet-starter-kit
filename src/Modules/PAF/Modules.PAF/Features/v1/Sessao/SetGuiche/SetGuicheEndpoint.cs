using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Sessao;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Sessao.SetGuiche;

public static class SetGuicheEndpoint
{
    internal static RouteHandlerBuilder MapSetGuicheEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/guiche",
                async (SetGuicheCommand command, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(command, ct);
                    return Results.NoContent();
                })
            .WithName("PafSetGuiche")
            .WithSummary("Seleciona o guichê da sessão na unidade corrente")
            .RequirePermission(PafSessaoPermissions.Guiches.Selecionar);
    }
}
