using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Parametros;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Parametros.SetParametroEmpresa;

public static class SetParametroEmpresaEndpoint
{
    internal static RouteHandlerBuilder MapSetParametroEmpresaEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPut("/parametros/empresa/{chave}",
                async (string chave, ParametroValorRequest corpo, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(new SetParametroEmpresaCommand(chave, corpo.Valor), ct);
                    return Results.NoContent();
                })
            .WithName("PafSetParametroEmpresa")
            .WithSummary("Write the company-level default of an RF06 parameter")
            .RequirePermission(PafPermissions.Parametros.UpdateEmpresa);
}
