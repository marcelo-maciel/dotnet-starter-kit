using FSH.Framework.Core.Exceptions;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.v1.UsuariosUnidade;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.UsuariosUnidade.SetUsuarioUnidades;

/// <summary>
/// Scope is the boundary of granting and of editing (arquitetura seção 5): the caller only grants
/// unidades inside its own scope, and only edits a user whose whole current scope is inside it.
/// </summary>
public sealed class SetUsuarioUnidadesCommandHandler(PafDbContext db, EscopoUsuario escopo, IUserProfileService userService)
    : ICommandHandler<SetUsuarioUnidadesCommand, Guid>
{
    public async ValueTask<Guid> Handle(SetUsuarioUnidadesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Throws NotFound for a user outside the request's tenant.
        await userService.GetAsync(command.UsuarioId.ToString(), cancellationToken).ConfigureAwait(false);

        var doChamador = (await escopo.UnidadesAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var atuais = await db.UsuariosUnidade
            .Where(u => u.UsuarioId == command.UsuarioId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!atuais.All(a => doChamador.Contains(a.UnidadeId)) || !command.Unidades.All(doChamador.Contains))
        {
            throw new ForbiddenException("Unidade fora do escopo de quem concede.");
        }

        var ordemDesejada = command.Unidades.Select((unidadeId, ordem) => (unidadeId, ordem)).ToDictionary(x => x.unidadeId, x => x.ordem);
        foreach (var atual in atuais)
        {
            if (ordemDesejada.Remove(atual.UnidadeId, out int ordem))
            {
                atual.DefinirOrdem(ordem);
            }
            else
            {
                db.UsuariosUnidade.Remove(atual);
            }
        }

        db.UsuariosUnidade.AddRange(ordemDesejada.Select(d => UsuarioUnidade.Criar(command.UsuarioId, d.Key, d.Value)));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return command.UsuarioId;
    }
}
