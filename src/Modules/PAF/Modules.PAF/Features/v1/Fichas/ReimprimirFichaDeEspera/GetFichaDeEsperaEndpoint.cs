using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Fichas.ReimprimirFichaDeEspera;

public static class GetFichaDeEsperaEndpoint
{
    internal static RouteHandlerBuilder MapGetFichaDeEsperaEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/fichas/reimpressao",
                async ([AsParameters] ReimpressaoFichaQuery query, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(query, ct)))
            .WithName("PafGetFichaDeEspera")
            .WithSummary("Stored ficha of the current unit by printed senha, day and fila, for a reprint; never a new number (RF20)")
            .RequirePermission(PafPermissions.FichasDoDia.View)
            .Produces<IReadOnlyList<FichaEmitidaDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
}
