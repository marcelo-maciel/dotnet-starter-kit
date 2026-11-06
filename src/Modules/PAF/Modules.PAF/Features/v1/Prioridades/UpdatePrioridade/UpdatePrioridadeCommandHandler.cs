using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Prioridades;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Prioridades.UpdatePrioridade;

public sealed class UpdatePrioridadeCommandHandler(PafDbContext db, IUnidadeCorrente unidadeCorrente)
    : ICommandHandler<UpdatePrioridadeCommand, Guid>
{
    public async ValueTask<Guid> Handle(UpdatePrioridadeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        unidadeCorrente.Exigir();

        var prioridade = await db.Prioridades
            .FirstOrDefaultAsync(p => p.Id == command.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Prioridade {command.Id} não encontrada.");

        prioridade.Atualizar(command.Nome.Trim(), command.Valor);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return prioridade.Id;
    }
}
