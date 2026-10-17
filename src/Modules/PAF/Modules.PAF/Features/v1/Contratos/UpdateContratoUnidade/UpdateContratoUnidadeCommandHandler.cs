using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Contratos;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Contratos.UpdateContratoUnidade;

/// <summary>
/// Operator path of arquitetura seção 6: no current unidade, so only the unit filter is turned off, by
/// name; the tenant filter keeps the operator inside the one client resolved for this request.
/// </summary>
public sealed class UpdateContratoUnidadeCommandHandler(PafDbContext db)
    : ICommandHandler<UpdateContratoUnidadeCommand, Guid>
{
    public async ValueTask<Guid> Handle(UpdateContratoUnidadeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var contrato = await db.ContratosUnidade.IgnoreQueryFilters([PafQueryFilters.Unidade])
            .FirstOrDefaultAsync(c => c.Id == command.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Contrato {command.Id} não encontrado.");

        contrato.Atualizar(
            command.InicioVigencia,
            command.FimVigencia,
            command.Plano.Trim(),
            command.LimiteSessoes,
            Enum.Parse<PoliticaLimiteLicenca>(command.Politica));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return contrato.Id;
    }
}
