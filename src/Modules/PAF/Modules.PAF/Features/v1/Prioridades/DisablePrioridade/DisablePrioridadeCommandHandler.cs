using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Prioridades;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Prioridades.DisablePrioridade;

public sealed class DisablePrioridadeCommandHandler(PafDbContext db, IUnidadeCorrente unidadeCorrente)
    : ICommandHandler<DisablePrioridadeCommand, Guid>
{
    public async ValueTask<Guid> Handle(DisablePrioridadeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        unidadeCorrente.Exigir();

        var entidade = await db.Prioridades
            .FirstOrDefaultAsync(e => e.Id == command.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Prioridade {command.Id} não encontrada.");

        entidade.Desativar();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return entidade.Id;
    }
}
