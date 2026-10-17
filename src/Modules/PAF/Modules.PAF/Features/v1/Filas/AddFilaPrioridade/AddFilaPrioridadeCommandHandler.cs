using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Filas;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Filas.AddFilaPrioridade;

public sealed class AddFilaPrioridadeCommandHandler(PafDbContext db, IUnidadeCorrente unidadeCorrente)
    : ICommandHandler<AddFilaPrioridadeCommand, Guid>
{
    public async ValueTask<Guid> Handle(AddFilaPrioridadeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var unidadeId = unidadeCorrente.Exigir();

        bool filaExiste = await db.Filas.AnyAsync(f => f.Id == command.FilaId, cancellationToken).ConfigureAwait(false);
        bool prioridadeExiste = await db.Prioridades.AnyAsync(p => p.Id == command.PrioridadeId, cancellationToken).ConfigureAwait(false);
        if (!filaExiste || !prioridadeExiste)
        {
            throw new NotFoundException("Fila ou prioridade não encontrada nesta unidade.");
        }

        string letra = LetraDaSenha.Normalizar(command.Letra);
        await LetraDaSenha.ExigirLivresAsync(db, [letra], null, cancellationToken).ConfigureAwait(false);

        var item = FilaPrioridade.Criar(unidadeId, command.FilaId, command.PrioridadeId, letra, command.NumeroInicial, command.Ordem);
        db.FilaPrioridades.Add(item);
        await LetraDaSenha.SalvarAsync(db, cancellationToken).ConfigureAwait(false);
        return item.Id;
    }
}
