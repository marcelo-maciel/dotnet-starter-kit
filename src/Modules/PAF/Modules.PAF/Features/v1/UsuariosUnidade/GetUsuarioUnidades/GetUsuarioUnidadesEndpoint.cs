using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.UsuariosUnidade;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.UsuariosUnidade.GetUsuarioUnidades;

public static class GetUsuarioUnidadesEndpoint
{
    internal static RouteHandlerBuilder MapGetUsuarioUnidadesEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/usuarios/{usuarioId:guid}/unidades",
                async (Guid usuarioId, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new GetUsuarioUnidadesQuery(usuarioId), ct)))
            .WithName("PafGetUsuarioUnidades")
            .WithSummary("Get a user's ordered unit scope")
            .RequirePermission(PafPermissions.UsuariosUnidade.View);
}
