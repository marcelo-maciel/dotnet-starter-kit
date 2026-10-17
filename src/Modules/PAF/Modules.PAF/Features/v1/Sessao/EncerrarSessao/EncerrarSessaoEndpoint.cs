using FSH.Modules.PAF.Contracts.v1.Sessao;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Sessao.EncerrarSessao;

public static class EncerrarSessaoEndpoint
{
    internal static RouteHandlerBuilder MapEncerrarSessaoEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/logout",
                async (IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(new EncerrarSessaoCommand(), ct);
                    return Results.NoContent();
                })
            .WithName("PafEncerrarSessao")
            .WithSummary("Logout pelo servidor: revoga a sessão e solta licença e guichê");
    }
}
