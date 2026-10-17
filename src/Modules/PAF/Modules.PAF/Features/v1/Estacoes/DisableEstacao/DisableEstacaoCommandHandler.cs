using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estacoes.DisableEstacao;

public sealed class DisableEstacaoCommandHandler(PafDbContext db, IUnidadeCorrente unidadeCorrente)
    : ICommandHandler<DisableEstacaoCommand, Guid>
{
    public async ValueTask<Guid> Handle(DisableEstacaoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        unidadeCorrente.Exigir();

        var entidade = await db.Estacoes
            .FirstOrDefaultAsync(e => e.Id == command.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Estacao {command.Id} não encontrada.");

        entidade.Desativar();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return entidade.Id;
    }
}
