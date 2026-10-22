using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.CancelarAtendimento;

public static class VoidAtendimentoEndpoint
{
    internal static RouteHandlerBuilder MapVoidAtendimentoEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/atendimentos/{atendimentoId:guid}/cancelar",
                async (Guid atendimentoId, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new CancelarAtendimentoCommand(atendimentoId), ct)))
            .WithName("PafVoidAtendimento")
            .WithSummary("Cancel the called atendimento as a no-show (RF15)")
            .RequirePermission(PafPermissions.Atendimentos.Operar);
    }
}
