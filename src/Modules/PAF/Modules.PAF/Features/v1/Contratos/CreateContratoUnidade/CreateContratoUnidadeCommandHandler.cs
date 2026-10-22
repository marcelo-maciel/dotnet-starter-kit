using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Contratos;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Contratos.CreateContratoUnidade;

/// <summary>
/// The Codefi operator has no current unidade, so the unidade comes in the command, checked under the
/// client's tenant filter (Unidade has no unit filter). Gated by a root-only permission (RF23).
/// </summary>
public sealed class CreateContratoUnidadeCommandHandler(
    PafDbContext db,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : ICommandHandler<CreateContratoUnidadeCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreateContratoUnidadeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id
            ?? throw new InvalidOperationException("Contrato sem tenant resolvido.");

        bool unidadeExiste = await db.Unidades.AnyAsync(u => u.Id == command.UnidadeId, cancellationToken).ConfigureAwait(false);
        if (!unidadeExiste)
        {
            throw new NotFoundException($"Unidade {command.UnidadeId} não encontrada.");
        }

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await VigenciaContrato.TravarUnidadeAsync(db, tenantId, command.UnidadeId, cancellationToken).ConfigureAwait(false);
        await VigenciaContrato.ExigirSemSobreposicaoAsync(
            db, command.UnidadeId, null, command.InicioVigencia, command.FimVigencia, cancellationToken).ConfigureAwait(false);

        var contrato = ContratoUnidade.Criar(
            command.UnidadeId,
            command.InicioVigencia,
            command.FimVigencia,
            command.Plano.Trim(),
            command.LimiteSessoes,
            Enum.Parse<PoliticaLimiteLicenca>(command.Politica));
        db.ContratosUnidade.Add(contrato);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        return contrato.Id;
    }
}
