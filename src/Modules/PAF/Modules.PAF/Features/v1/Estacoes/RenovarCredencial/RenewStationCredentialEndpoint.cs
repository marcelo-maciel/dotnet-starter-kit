using FSH.Modules.PAF.Contracts.v1.Estacoes;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Estacoes.RenovarCredencial;

public static class RenewStationCredentialEndpoint
{
    internal static RouteHandlerBuilder MapRenewStationCredentialEndpoint(this IEndpointRouteBuilder endpoints)
    {
        // Anonymous on purpose: the access token may already be expired; the secret is the credential.
        return endpoints.MapPost("/paf/estacoes/credencial/renovar",
                async (RenovarCredencialEstacaoCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command, ct)))
            .WithName("RenewStationCredential")
            .WithSummary("Issue a new station access token from the credential secret")
            .AllowAnonymous()
            .RequireRateLimiting("auth");
    }
}
