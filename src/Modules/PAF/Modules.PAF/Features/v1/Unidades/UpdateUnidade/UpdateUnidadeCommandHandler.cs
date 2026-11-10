using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Unidades;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Unidades.UpdateUnidade;

public sealed class UpdateUnidadeCommandHandler(PafDbContext db, EscopoUsuario escopo)
    : ICommandHandler<UpdateUnidadeCommand, Guid>
{
    public async ValueTask<Guid> Handle(UpdateUnidadeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await escopo.ExigirAsync(command.Id, cancellationToken).ConfigureAwait(false);

        var unidade = await db.Unidades
            .FirstOrDefaultAsync(u => u.Id == command.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Unidade {command.Id} não encontrada.");

        unidade.Atualizar(command.Nome.Trim(), command.DocumentoFiscal?.Trim());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return unidade.Id;
    }
}
