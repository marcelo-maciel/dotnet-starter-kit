using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estacoes.RevogarVinculo;

public sealed class RevogarVinculoEstacaoCommandHandler(PafDbContext db)
    : ICommandHandler<RevogarVinculoEstacaoCommand>
{
    public async ValueTask<Unit> Handle(RevogarVinculoEstacaoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Unit-filtered like generation: a station outside the current unit is indistinguishable from a missing one.
        var existe = await db.Estacoes
            .AnyAsync(e => e.Id == command.EstacaoId, cancellationToken)
            .ConfigureAwait(false);
        if (!existe)
        {
            throw new NotFoundException($"Estacao {command.EstacaoId} not found.");
        }

        // Idempotent: revoking a station with no current link is a no-op, not an error.
        await RevogacaoVinculo.RevogarVigentesAsync(db, command.EstacaoId, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
