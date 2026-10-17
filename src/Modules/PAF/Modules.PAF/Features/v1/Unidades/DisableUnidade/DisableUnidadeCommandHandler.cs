using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Unidades;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Unidades.DisableUnidade;

public sealed class DisableUnidadeCommandHandler(PafDbContext db, EscopoUsuario escopo)
    : ICommandHandler<DisableUnidadeCommand, Guid>
{
    public async ValueTask<Guid> Handle(DisableUnidadeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await escopo.ExigirAsync(command.Id, cancellationToken).ConfigureAwait(false);

        var entidade = await db.Unidades
            .FirstOrDefaultAsync(e => e.Id == command.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Unidade {command.Id} não encontrada.");

        entidade.Desativar();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return entidade.Id;
    }
}
