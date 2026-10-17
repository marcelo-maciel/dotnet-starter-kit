using System.Security.Claims;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Estacoes.ObterConfiguracao;

// One route per role so each policy admits exactly one credential (least privilege, arquitetura seção 5).
// The station id comes from the credential, never from the route: a station reads only itself.
public static class GetStationEmissionSettingsEndpoint
{
    internal static RouteHandlerBuilder MapGetStationEmissionSettingsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/paf/estacao/emissao/configuracao",
                async (ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ObterConfiguracaoEstacaoQuery(Guid.Parse(user.FindFirstValue(PafClaims.Estacao)!)), ct)))
            .WithName("GetStationEmissionSettings")
            .WithSummary("Read the emission station's own settings with its station credential")
            .RequireAuthorization(PafPolicies.EstacaoEmissao);
    }
}

public static class GetStationPanelSettingsEndpoint
{
    internal static RouteHandlerBuilder MapGetStationPanelSettingsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/paf/estacao/painel/configuracao",
                async (ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ObterConfiguracaoEstacaoQuery(Guid.Parse(user.FindFirstValue(PafClaims.Estacao)!)), ct)))
            .WithName("GetStationPanelSettings")
            .WithSummary("Read the panel station's own settings with its station credential")
            .RequireAuthorization(PafPolicies.EstacaoPainel);
    }
}
