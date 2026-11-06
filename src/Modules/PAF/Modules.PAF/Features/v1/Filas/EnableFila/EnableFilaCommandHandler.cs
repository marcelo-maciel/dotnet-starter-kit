using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Filas;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Filas.EnableFila;

public sealed class EnableFilaCommandHandler(PafDbContext db, IUnidadeCorrente unidadeCorrente)
    : ICommandHandler<EnableFilaCommand, Guid>
{
    public async ValueTask<Guid> Handle(EnableFilaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        unidadeCorrente.Exigir();

        var entidade = await db.Filas
            .FirstOrDefaultAsync(e => e.Id == command.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Fila {command.Id} não encontrada.");

        entidade.Ativar();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return entidade.Id;
    }
}
