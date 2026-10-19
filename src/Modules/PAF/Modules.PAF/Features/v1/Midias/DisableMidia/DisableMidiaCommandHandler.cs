using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Midias.DisableMidia;

/// <summary>There is no deletion (RF03): a disabled mídia keeps its object, programações and painel lists skip it.</summary>
public sealed class DisableMidiaCommandHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : ICommandHandler<DisableMidiaCommand, MidiaDto>
{
    public async ValueTask<MidiaDto> Handle(DisableMidiaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var unidadeId = unidadeCorrente.Exigir();
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var midia = await MidiaTravada.TravarAsync(db, tenantId, unidadeId, command.Id, cancellationToken).ConfigureAwait(false);
        midia.Desativar();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        return MidiaTravada.ParaDto(midia);
    }
}
