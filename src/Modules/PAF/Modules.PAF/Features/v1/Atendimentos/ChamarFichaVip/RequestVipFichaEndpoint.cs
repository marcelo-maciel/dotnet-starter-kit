using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ChamarFichaVip;

public static class RequestVipFichaEndpoint
{
    internal static RouteHandlerBuilder MapRequestVipFichaEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/vip",
                async (ChamarFichaVipCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command, ct)))
            .WithName("PafRequestVipFicha")
            .WithSummary("Call the ficha named by senha, date and fila on the session guichê, out of queue order (RF19 VIP)")
            .RequirePermission(PafPermissions.Atendimentos.Vip);
}
