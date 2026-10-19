using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.ProgramacoesMidia;

/// <summary>
/// RF10 physical deletion. Tracked <c>Remove</c>, so the impersonation act rides on the same <c>SaveChanges</c>; under
/// the row lock, a concurrent edit either lands first (and is deleted with the row) or then finds nothing (404).
/// </summary>
public sealed class DeleteProgramacaoMidiaCommandHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : ICommandHandler<DeleteProgramacaoMidiaCommand>
{
    public async ValueTask<Unit> Handle(DeleteProgramacaoMidiaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var unidadeId = unidadeCorrente.Exigir();
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var programacao = await ProgramacaoTravada.TravarAsync(db, tenantId, unidadeId, command.Id, cancellationToken).ConfigureAwait(false);
        db.ProgramacoesMidia.Remove(programacao);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
