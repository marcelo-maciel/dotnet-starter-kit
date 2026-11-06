using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Usuarios;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Usuarios.Permissoes;

/// <summary>Body of the per-user grant, so the route id stays the only user id of the request.</summary>
public sealed record DefinirPermissoesUsuarioPafRequest(IReadOnlyList<string> Permissoes);

/// <summary>RF08 per-user permissions (C15) under <c>api/v1/paf/usuarios/{id}/permissoes</c>.</summary>
public static class PermissoesUsuarioPafEndpoints
{
    internal static void MapPermissoesUsuarioPafEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/usuarios/{usuarioId:guid}/permissoes",
                async (Guid usuarioId, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new GetPermissoesUsuarioPafQuery(usuarioId), ct)))
            .WithName("PafGetPermissoesUsuario")
            .WithSummary("The four RF08 permission groups and the user's role and individual permissions")
            .RequirePermission(PafPermissions.Usuarios.View);

        endpoints.MapPut("/usuarios/{usuarioId:guid}/permissoes",
                async (Guid usuarioId, DefinirPermissoesUsuarioPafRequest corpo, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new DefinirPermissoesUsuarioPafCommand(usuarioId, corpo.Permissoes ?? []), ct)))
            .WithName("PafDefinirPermissoesUsuario")
            .WithSummary("Replace the user's individual PAF permissions")
            .RequirePermission(PafPermissions.Usuarios.Manage);
    }
}
