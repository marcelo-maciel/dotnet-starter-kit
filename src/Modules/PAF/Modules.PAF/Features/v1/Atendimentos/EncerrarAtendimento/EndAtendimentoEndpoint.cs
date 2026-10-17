using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.EncerrarAtendimento;

public static class EndAtendimentoEndpoint
{
    internal static RouteHandlerBuilder MapEndAtendimentoEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/atendimentos/{atendimentoId:guid}/encerrar",
                async (Guid atendimentoId, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new EncerrarAtendimentoCommand(atendimentoId), ct)))
            .WithName("PafEndAtendimento")
            .WithSummary("Close the started atendimento on the session guichê (RF16)")
            .RequirePermission(PafPermissions.Atendimentos.Operar);
    }
}
