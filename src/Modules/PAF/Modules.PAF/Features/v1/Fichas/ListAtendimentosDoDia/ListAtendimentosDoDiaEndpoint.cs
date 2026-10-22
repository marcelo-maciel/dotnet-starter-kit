using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Fichas.ListAtendimentosDoDia;

public static class ListAtendimentosDoDiaEndpoint
{
    internal static RouteHandlerBuilder MapListAtendimentosDoDiaEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/fichas/atendimentos",
                async ([AsParameters] ListAtendimentosDoDiaQuery query, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(query, ct)))
            .WithName("PafListAtendimentosDoDia")
            .WithSummary("Atendimentos of the current unit on a day, by fila and estado, with the waiting total (RF12)")
            .RequirePermission(PafPermissions.FichasDoDia.View)
            .Produces<FichasDoDiaDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
}
