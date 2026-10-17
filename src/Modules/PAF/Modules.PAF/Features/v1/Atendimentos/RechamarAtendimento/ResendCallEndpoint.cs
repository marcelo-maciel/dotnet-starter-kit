using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.RechamarAtendimento;

public static class ResendCallEndpoint
{
    internal static RouteHandlerBuilder MapResendCallEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/atendimentos/{atendimentoId:guid}/rechamar",
                async (Guid atendimentoId, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new RechamarAtendimentoCommand(atendimentoId), ct)))
            .WithName("PafResendCall")
            .WithSummary("Announce the called ficha again on the painéis (RF21)")
            .RequirePermission(PafPermissions.Atendimentos.Operar);
    }
}
