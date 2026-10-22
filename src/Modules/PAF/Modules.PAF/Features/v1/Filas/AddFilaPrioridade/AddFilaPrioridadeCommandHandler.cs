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

        var fila = await db.Filas.AsNoTracking()
            .Where(f => f.Id == command.FilaId)
            .Select(f => new { f.CorFonte })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        string? prioridade = await db.Prioridades.AsNoTracking()
            .Where(p => p.Id == command.PrioridadeId)
            .Select(p => p.Nome)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (fila is null || prioridade is null)
        {
            throw new NotFoundException("Fila ou prioridade não encontrada nesta unidade.");
        }

        AparenciaDaFila.ExigirContraste(fila.CorFonte, [(prioridade, command.CorBotao)]);

        string letra = LetraDaSenha.Normalizar(command.Letra);
        await LetraDaSenha.ExigirLivresAsync(db, [letra], null, cancellationToken).ConfigureAwait(false);

        var item = FilaPrioridade.Criar(unidadeId, command.FilaId, command.PrioridadeId, letra, command.NumeroInicial, command.Ordem);
        item.DefinirCorBotao(command.CorBotao);
        db.FilaPrioridades.Add(item);
        await LetraDaSenha.SalvarAsync(db, cancellationToken).ConfigureAwait(false);
        return item.Id;
    }
}
