using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.UsuariosUnidade;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.UsuariosUnidade.SetUsuarioUnidades;

public static class SetUsuarioUnidadesEndpoint
{
    internal static RouteHandlerBuilder MapSetUsuarioUnidadesEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPut("/usuarios/{usuarioId:guid}/unidades",
                async (Guid usuarioId, SetUsuarioUnidadesCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command with { UsuarioId = usuarioId }, ct)))
            .WithName("PafSetUsuarioUnidades")
            .WithSummary("Replace a user's ordered unit scope")
            .RequirePermission(PafPermissions.UsuariosUnidade.Update);
}
