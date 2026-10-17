using FSH.Modules.PAF.Contracts.v1.Estacoes;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Estacoes.ResgatarVinculo;

public static class IssueStationCredentialEndpoint
{
    internal static RouteHandlerBuilder MapIssueStationCredentialEndpoint(this IEndpointRouteBuilder endpoints)
    {
        // Anonymous like the kit's token issue: the tenant comes from the `tenant` header or ?tenant= of the link.
        return endpoints.MapPost("/paf/estacoes/vinculo/resgatar",
                async (ResgatarVinculoEstacaoCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command, ct)))
            .WithName("IssueStationCredential")
            .WithSummary("Redeem a one-time station link for one credential per station role")
            .AllowAnonymous()
            .RequireRateLimiting("auth");
    }
}
