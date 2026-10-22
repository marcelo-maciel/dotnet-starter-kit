using FSH.Modules.PAF.Contracts.v1.Sessao;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Sessao.GetAvisoLicenca;

public static class GetAvisoLicencaEndpoint
{
    // No permission of its own, like the other session endpoints: it reads only the caller's own session.
    internal static RouteHandlerBuilder MapGetAvisoLicencaEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/aviso-licenca",
                async (IMediator mediator, CancellationToken ct) =>
                    await mediator.Send(new GetAvisoLicencaQuery(), ct) is { } aviso ? Results.Ok(aviso) : Results.NoContent())
            .WithName("PafGetAvisoLicenca")
            .WithSummary("Aviso da sessão que entrou acima do limite de licenças da unidade; 204 sem excedente");
}
