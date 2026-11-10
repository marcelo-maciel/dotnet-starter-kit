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

        var fila = await db.Filas.AsNoTracking()
            .Where(f => f.Id == command.FilaId)
            .Select(f => new { f.CorFonte })
            .FirstAsync(cancellationToken)
            .ConfigureAwait(false);
        string prioridade = await db.Prioridades.AsNoTracking()
            .Where(p => p.Id == command.PrioridadeId)
            .Select(p => p.Nome)
            .FirstAsync(cancellationToken)
            .ConfigureAwait(false);
        AparenciaDaFila.ExigirContraste(fila.CorFonte, [(prioridade, command.CorBotao)]);

        string letra = LetraDaSenha.Normalizar(command.Letra);
        await LetraDaSenha.ExigirLivresAsync(db, [letra], item.Id, cancellationToken).ConfigureAwait(false);

        item.Atualizar(letra, command.NumeroInicial, command.Ordem);
        item.DefinirCorBotao(command.CorBotao);
        await LetraDaSenha.SalvarAsync(db, cancellationToken).ConfigureAwait(false);
        return item.Id;
    }
}
