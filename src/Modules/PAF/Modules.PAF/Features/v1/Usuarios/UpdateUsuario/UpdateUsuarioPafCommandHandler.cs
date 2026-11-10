using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.v1.Usuarios;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Usuarios.UpdateUsuario;

/// <summary>RF08: only a user whose whole scope fits in the caller's is edited (403 otherwise).</summary>
public sealed class UpdateUsuarioPafCommandHandler(PafDbContext db, EscopoUsuario escopo, IUserProfileService perfis)
    : ICommandHandler<UpdateUsuarioPafCommand, Guid>
{
    public async ValueTask<Guid> Handle(UpdateUsuarioPafCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await escopo.ExigirAlcanceSobreAsync(command.UsuarioId, cancellationToken).ConfigureAwait(false);

        // Throws NotFound for a user outside the request's tenant.
        var atual = await perfis.GetAsync(command.UsuarioId.ToString(), cancellationToken).ConfigureAwait(false);

        // The kit update is a full representation: the phone goes back as it was, and a null image means "keep it"
        // (the kit's own update endpoint passes it the same way).
        await perfis.UpdateAsync(
            command.UsuarioId.ToString(), command.Nome.Trim(), command.Sobrenome.Trim(), atual.PhoneNumber ?? string.Empty,
            image: null!, deleteCurrentImage: false, locale: null, expectedConcurrencyStamps: null, cancellationToken).ConfigureAwait(false);

        var perfil = await db.PerfisUsuario.FirstOrDefaultAsync(p => p.UsuarioId == command.UsuarioId, cancellationToken).ConfigureAwait(false);
        if (perfil is null)
        {
            perfil = PerfilUsuario.Criar(command.UsuarioId);
            db.PerfisUsuario.Add(perfil);
        }

        perfil.DefinirFuncaoERamal(command.Funcao, command.Ramal);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return command.UsuarioId;
    }
}
