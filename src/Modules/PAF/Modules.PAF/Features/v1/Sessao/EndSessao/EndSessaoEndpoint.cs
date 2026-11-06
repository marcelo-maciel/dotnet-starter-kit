using FSH.Modules.PAF.Contracts.v1.Sessao;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Sessao.EndSessao;

public static class EndSessaoEndpoint
{
    /// <summary>Lido pelo SessionCheckMiddleware: o logout exige só a sessão ativa, sem marca de sessão viva.</summary>
    public const string NomeEndpoint = "PafEndSessao";

    internal static RouteHandlerBuilder MapEndSessaoEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/logout",
                async (IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(new EndSessaoCommand(), ct);
                    return Results.NoContent();
                })
            .WithName(NomeEndpoint)
            .WithSummary("Logout pelo servidor: revoga a sessão e solta licença e guichê");
    }
}
