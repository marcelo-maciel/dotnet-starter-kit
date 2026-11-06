using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Parametros;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Parametros.SetParametroUnidade;

public static class SetParametroUnidadeEndpoint
{
    internal static RouteHandlerBuilder MapSetParametroUnidadeEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPut("/parametros/unidade/{chave}",
                async (string chave, ParametroValorRequest corpo, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(new SetParametroUnidadeCommand(chave, corpo.Valor), ct);
                    return Results.NoContent();
                })
            .WithName("PafSetParametroUnidade")
            .WithSummary("Override an RF06 parameter in the current unit")
            .RequirePermission(PafPermissions.Parametros.UpdateUnidade);
}
