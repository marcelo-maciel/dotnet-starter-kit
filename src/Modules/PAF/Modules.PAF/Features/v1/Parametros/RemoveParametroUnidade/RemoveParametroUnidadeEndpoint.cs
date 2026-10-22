using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Parametros;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Parametros.RemoveParametroUnidade;

public static class RemoveParametroUnidadeEndpoint
{
    internal static RouteHandlerBuilder MapRemoveParametroUnidadeEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapDelete("/parametros/unidade/{chave}",
                async (string chave, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(new RemoveParametroUnidadeCommand(chave), ct);
                    return Results.NoContent();
                })
            .WithName("PafRemoveParametroUnidade")
            .WithSummary("Drop the current unit's override of an RF06 parameter, falling back to the company default")
            .RequirePermission(PafPermissions.Parametros.UpdateUnidade);
}
