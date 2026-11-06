using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Fichas.ConsultarFicha;

public static class GetFichaBySenhaEndpoint
{
    internal static RouteHandlerBuilder MapGetFichaBySenhaEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/fichas/consulta",
                async ([AsParameters] ConsultarFichaQuery query, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(query, ct)))
            .WithName("PafGetFichaBySenha")
            .WithSummary("Ficha of the current unit by printed senha and day, with all its atendimentos (RF18)")
            .RequirePermission(PafPermissions.FichasDoDia.View)
            .Produces<IReadOnlyList<FichaConsultadaDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
}
