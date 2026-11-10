using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Fichas.ObterEmissaoGuiche;

public static class GetEmissaoGuicheEndpoint
{
    // Under the emission permission, not FichasDoDia.View: the reprint of the last ficha comes in this read, so the
    // ordinary attendant (basic Fichas.Emitir) reprints without seeing the whole day's list.
    internal static RouteHandlerBuilder MapGetEmissaoGuicheEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/emissao",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ObterEmissaoGuicheQuery(), ct)))
            .WithName("PafGetEmissaoGuiche")
            .WithSummary("The guichê emission grid and the guichê's last ficha of today, as printed (RF11)")
            .RequirePermission(PafPermissions.Fichas.Emitir)
            .Produces<EmissaoGuicheDto>(StatusCodes.Status200OK);
}
