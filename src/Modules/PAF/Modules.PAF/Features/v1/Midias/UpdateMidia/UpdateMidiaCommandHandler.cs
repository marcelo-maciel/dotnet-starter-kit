using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Midias.UpdateMidia;

/// <summary>Description and display duration (RF03), refused with 409 when the version read is no longer current.</summary>
public sealed class UpdateMidiaCommandHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : ICommandHandler<UpdateMidiaCommand, MidiaDto>
{
    public async ValueTask<MidiaDto> Handle(UpdateMidiaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var unidadeId = unidadeCorrente.Exigir();
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var midia = await MidiaTravada.TravarAsync(db, tenantId, unidadeId, command.Id, cancellationToken).ConfigureAwait(false);
        MidiaTravada.ExigirVersao(midia, command.Versao);
        midia.Editar(command.Descricao.Trim(), command.DuracaoSegundos);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        return MidiaTravada.ParaDto(midia);
    }
}
