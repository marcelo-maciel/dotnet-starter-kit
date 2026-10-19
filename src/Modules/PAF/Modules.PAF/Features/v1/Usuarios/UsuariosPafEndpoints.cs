using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Usuarios;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Usuarios;

/// <summary>Body of the deactivation, so the route id stays the only user id of the request.</summary>
public sealed record DesativarUsuarioPafRequest(bool CancelarAtendimentosEmCurso);

/// <summary>Body of the profile copy onto the user of the route.</summary>
public sealed record CopiarPerfilUsuarioPafRequest(Guid UsuarioOrigemId, bool CancelarAtendimentosEmCurso);

/// <summary>Body of the user edit.</summary>
public sealed record UpdateUsuarioPafRequest(string Nome, string Sobrenome, string? Funcao, string? Ramal);

/// <summary>RF08 user cadastro (C12) under <c>api/v1/paf/usuarios</c>; the scope itself stays on <c>usuarios/{id}/unidades</c>.</summary>
public static class UsuariosPafEndpoints
{
    internal static void MapUsuariosPafEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/usuarios",
                async (IMediator mediator, CancellationToken ct) => Results.Ok(await mediator.Send(new ListUsuariosPafQuery(), ct)))
            .WithName("PafListUsuarios")
            .WithSummary("List the PAF users whose scope fits in the caller's")
            .RequirePermission(PafPermissions.Usuarios.View);

        endpoints.MapPost("/usuarios",
                async (CreateUsuarioPafCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command, ct)))
            .WithName("PafCreateUsuario")
            .WithSummary("Create a PAF user with role, profile and scope")
            .RequirePermission(PafPermissions.Usuarios.Manage);

        endpoints.MapPut("/usuarios/{usuarioId:guid}",
                async (Guid usuarioId, UpdateUsuarioPafRequest corpo, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new UpdateUsuarioPafCommand(usuarioId, corpo.Nome, corpo.Sobrenome, corpo.Funcao, corpo.Ramal), ct)))
            .WithName("PafUpdateUsuario")
            .WithSummary("Update a PAF user's name, job title and extension")
            .RequirePermission(PafPermissions.Usuarios.Manage);

        endpoints.MapPost("/usuarios/{usuarioId:guid}/desativar",
                async (Guid usuarioId, DesativarUsuarioPafRequest corpo, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new DesativarUsuarioPafCommand(usuarioId, corpo.CancelarAtendimentosEmCurso), ct)))
            .WithName("PafDesativarUsuario")
            .WithSummary("Deactivate a PAF user and revoke his sessions")
            .RequirePermission(PafPermissions.Usuarios.Manage);

        endpoints.MapPost("/usuarios/{usuarioId:guid}/copiar-perfil",
                async (Guid usuarioId, CopiarPerfilUsuarioPafRequest corpo, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(
                        new CopiarPerfilUsuarioPafCommand(usuarioId, corpo.UsuarioOrigemId, corpo.CancelarAtendimentosEmCurso), ct)))
            .WithName("PafCopiarPerfilUsuario")
            .WithSummary("Copy another user's PAF roles and scope onto this user")
            .RequirePermission(PafPermissions.Usuarios.Manage);

        endpoints.MapGet("/usuarios/{usuarioId:guid}/atendimentos-em-curso",
                async (Guid usuarioId, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ListAtendimentosEmCursoDoUsuarioQuery(usuarioId), ct)))
            .WithName("PafListAtendimentosEmCursoDoUsuario")
            .WithSummary("List the atendimentos a user holds Chamado or EmAtendimento, in any unit")
            .RequirePermission(PafPermissions.Usuarios.View);

        endpoints.MapPost("/usuarios/{usuarioId:guid}/senha",
                async (Guid usuarioId, DefinirSenhaUsuarioPafRequest corpo, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new DefinirSenhaUsuarioPafCommand(usuarioId, corpo.Senha, corpo.ConfirmacaoSenha), ct)))
            .WithName("PafDefinirSenhaUsuario")
            .WithSummary("Set another user's password and revoke his sessions")
            .RequirePermission(PafPermissions.Usuarios.Manage);
    }
}

/// <summary>Body of the password definition (C16), so the route id stays the only user id of the request.</summary>
public sealed record DefinirSenhaUsuarioPafRequest(string Senha, string ConfirmacaoSenha)
{
    // The record's generated ToString would print the password wherever the body gets formatted.
    public override string ToString() => nameof(DefinirSenhaUsuarioPafRequest);
}
