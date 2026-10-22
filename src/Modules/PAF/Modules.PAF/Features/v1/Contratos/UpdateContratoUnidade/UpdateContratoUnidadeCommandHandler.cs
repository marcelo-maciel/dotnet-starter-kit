using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
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
public sealed class UpdateContratoUnidadeCommandHandler(
    PafDbContext db,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : ICommandHandler<UpdateContratoUnidadeCommand, Guid>
{
    public async ValueTask<Guid> Handle(UpdateContratoUnidadeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id
            ?? throw new InvalidOperationException("Contrato sem tenant resolvido.");

        var unidadeId = await db.ContratosUnidade.IgnoreQueryFilters([PafQueryFilters.Unidade]).AsNoTracking()
            .Where(c => c.Id == command.Id)
            .Select(c => (Guid?)c.UnidadeId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Contrato {command.Id} não encontrado.");

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await VigenciaContrato.TravarUnidadeAsync(db, tenantId, unidadeId, cancellationToken).ConfigureAwait(false);
        await VigenciaContrato.ExigirSemSobreposicaoAsync(
            db, unidadeId, command.Id, command.InicioVigencia, command.FimVigencia, cancellationToken).ConfigureAwait(false);

        // Read again under the lock: the row the first read saw may have changed since.
        var contrato = await db.ContratosUnidade.IgnoreQueryFilters([PafQueryFilters.Unidade])
            .FirstAsync(c => c.Id == command.Id, cancellationToken)
            .ConfigureAwait(false);
        contrato.Atualizar(
            command.InicioVigencia,
            command.FimVigencia,
            command.Plano.Trim(),
            command.LimiteSessoes,
            Enum.Parse<PoliticaLimiteLicenca>(command.Politica));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        return contrato.Id;
    }
}
