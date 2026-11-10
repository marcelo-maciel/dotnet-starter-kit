using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ChamarFichaVip;

public static class ListVipFilasEndpoint
{
    internal static RouteHandlerBuilder MapListVipFilasEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/vip/filas",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ListFilasVipQuery(), ct)))
            .WithName("PafListVipFilas")
            .WithSummary("List the filas the session guichê serves, for the VIP form (RF19)")
            .RequirePermission(PafPermissions.Atendimentos.Vip);
}
