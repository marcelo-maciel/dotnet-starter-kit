using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Midias.EnableMidia;

public sealed class EnableMidiaCommandHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : ICommandHandler<EnableMidiaCommand, MidiaDto>
{
    public async ValueTask<MidiaDto> Handle(EnableMidiaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var unidadeId = unidadeCorrente.Exigir();
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var midia = await MidiaTravada.TravarAsync(db, tenantId, unidadeId, command.Id, cancellationToken).ConfigureAwait(false);
        midia.Ativar();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        return MidiaTravada.ParaDto(midia);
    }
}
