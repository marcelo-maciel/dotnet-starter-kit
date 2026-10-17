using FSH.Modules.PAF.Contracts.v1.Sessao;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Sessao.SelecionarUnidade;

public static class SelecionarUnidadeEndpoint
{
    internal static RouteHandlerBuilder MapSelecionarUnidadeEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/unidade-corrente",
                async (SelecionarUnidadeCommand command, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(command, ct);
                    return Results.NoContent();
                })
            .WithName("PafSelecionarUnidade")
            .WithSummary("Troca a unidade corrente da sessão");
    }
}
