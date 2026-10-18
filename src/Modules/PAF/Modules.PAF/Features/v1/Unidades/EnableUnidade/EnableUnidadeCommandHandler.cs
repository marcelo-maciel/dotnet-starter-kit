using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Unidades;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Unidades.EnableUnidade;

public sealed class EnableUnidadeCommandHandler(PafDbContext db, EscopoUsuario escopo)
    : ICommandHandler<EnableUnidadeCommand, Guid>
{
    public async ValueTask<Guid> Handle(EnableUnidadeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await escopo.ExigirAdministracaoDeUnidadesAsync(cancellationToken).ConfigureAwait(false);

        var entidade = await db.Unidades
            .FirstOrDefaultAsync(e => e.Id == command.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Unidade {command.Id} não encontrada.");

        entidade.Ativar();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return entidade.Id;
    }
}
