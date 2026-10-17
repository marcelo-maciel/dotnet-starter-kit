using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Filas;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Filas.UpdateFilaPrioridade;

public sealed class UpdateFilaPrioridadeCommandHandler(PafDbContext db, IUnidadeCorrente unidadeCorrente)
    : ICommandHandler<UpdateFilaPrioridadeCommand, Guid>
{
    public async ValueTask<Guid> Handle(UpdateFilaPrioridadeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        unidadeCorrente.Exigir();

        var item = await db.FilaPrioridades
            .FirstOrDefaultAsync(fp => fp.FilaId == command.FilaId && fp.PrioridadeId == command.PrioridadeId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("Prioridade não encontrada na grade desta fila.");

        string letra = LetraDaSenha.Normalizar(command.Letra);
        await LetraDaSenha.ExigirLivresAsync(db, [letra], item.Id, cancellationToken).ConfigureAwait(false);

        item.Atualizar(letra, command.NumeroInicial, command.Ordem);
        await LetraDaSenha.SalvarAsync(db, cancellationToken).ConfigureAwait(false);
        return item.Id;
    }
}
