using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ChamarProximaFicha;

public static class RequestNextFichaEndpoint
{
    internal static RouteHandlerBuilder MapRequestNextFichaEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/chamar-proximo",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ChamarProximaFichaCommand(), ct)))
            .WithName("PafRequestNextFicha")
            .WithSummary("Call the next ficha on the session guichê (RF15)")
            .RequirePermission(PafPermissions.Atendimentos.Operar);
    }
}
