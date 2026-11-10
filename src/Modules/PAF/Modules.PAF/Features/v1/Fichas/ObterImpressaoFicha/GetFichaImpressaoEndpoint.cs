using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Fichas.ObterImpressaoFicha;

public static class GetFichaImpressaoEndpoint
{
    internal static RouteHandlerBuilder MapGetFichaImpressaoEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/fichas/{fichaId:guid}/impressao",
                async (Guid fichaId, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ObterImpressaoFichaQuery(fichaId), ct)))
            .WithName("PafGetFichaImpressao")
            .WithSummary("The stored ficha of the current unit as printed, for a reprint; never a new number (RF11, RF12)")
            .RequirePermission(PafPermissions.FichasDoDia.View)
            .Produces<FichaEmitidaDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
}
