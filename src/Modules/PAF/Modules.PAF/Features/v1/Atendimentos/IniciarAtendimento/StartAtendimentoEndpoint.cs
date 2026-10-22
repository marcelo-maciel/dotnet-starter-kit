using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.IniciarAtendimento;

public static class StartAtendimentoEndpoint
{
    internal static RouteHandlerBuilder MapStartAtendimentoEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/atendimentos/{atendimentoId:guid}/iniciar",
                async (Guid atendimentoId, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new IniciarAtendimentoCommand(atendimentoId), ct)))
            .WithName("PafStartAtendimento")
            .WithSummary("Start the called atendimento on the session guichê")
            .RequirePermission(PafPermissions.Atendimentos.Operar);
    }
}
