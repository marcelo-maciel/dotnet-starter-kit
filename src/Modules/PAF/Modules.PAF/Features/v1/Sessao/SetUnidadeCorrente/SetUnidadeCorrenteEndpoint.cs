using FSH.Modules.PAF.Contracts.v1.Sessao;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Sessao.SetUnidadeCorrente;

public static class SetUnidadeCorrenteEndpoint
{
    internal static RouteHandlerBuilder MapSetUnidadeCorrenteEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/unidade-corrente",
                async (SetUnidadeCorrenteCommand command, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(command, ct);
                    return Results.NoContent();
                })
            .WithName("PafSetUnidadeCorrente")
            .WithSummary("Troca a unidade corrente da sessão");
    }
}
