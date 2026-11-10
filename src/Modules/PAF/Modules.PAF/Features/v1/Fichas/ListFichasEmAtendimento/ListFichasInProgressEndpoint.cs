using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Fichas.ListFichasEmAtendimento;

public static class ListFichasInProgressEndpoint
{
    internal static RouteHandlerBuilder MapListFichasInProgressEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/fichas/em-atendimento",
                async (Guid? filaId, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ListFichasEmAtendimentoQuery(filaId), ct)))
            .WithName("PafListFichasInProgress")
            .WithSummary("List the Chamado and EmAtendimento atendimentos of the current unit, longest stay first (RF13)")
            .RequirePermission(PafPermissions.FichasEmAtendimento.View);
}
