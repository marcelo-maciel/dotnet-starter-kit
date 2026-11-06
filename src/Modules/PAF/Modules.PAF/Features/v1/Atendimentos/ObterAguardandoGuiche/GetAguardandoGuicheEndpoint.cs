using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ObterAguardandoGuiche;

public static class GetAguardandoGuicheEndpoint
{
    internal static RouteHandlerBuilder MapGetAguardandoGuicheEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/aguardando",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ObterAguardandoGuicheQuery(), ct)))
            .WithName("PafGetAguardandoGuiche")
            .WithSummary("Count what the session guichê could call, in total or per fila (RF06, D30)")
            .RequirePermission(PafPermissions.Atendimentos.Operar);
}
