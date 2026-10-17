using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estacoes.UpdateEstacao;

public sealed class UpdateEstacaoCommandHandler(PafDbContext db, IUnidadeCorrente unidadeCorrente)
    : ICommandHandler<UpdateEstacaoCommand, Guid>
{
    public async ValueTask<Guid> Handle(UpdateEstacaoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        unidadeCorrente.Exigir();

        var estacao = await db.Estacoes
            .FirstOrDefaultAsync(e => e.Id == command.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Estação {command.Id} não encontrada.");

        await ListasDaEstacao.ValidarAsync(db, estacao.Tipo, command.Filas, command.Prioridades, cancellationToken).ConfigureAwait(false);

        estacao.Renomear(command.Nome.Trim());
        await ListasDaEstacao.SincronizarAsync(db, estacao, command.Filas, command.Prioridades, cancellationToken).ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return estacao.Id;
    }
}
