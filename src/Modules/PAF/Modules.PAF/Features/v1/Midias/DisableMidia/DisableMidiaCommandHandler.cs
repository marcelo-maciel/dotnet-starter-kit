using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.TempoReal;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Midias.DisableMidia;

/// <summary>There is no deletion (RF03): a disabled mídia keeps its object, programações and painel lists skip it.</summary>
public sealed class DisableMidiaCommandHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor,
    AvisosTempoReal avisos)
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
        var aviso = ProgramacaoAlteradaPaf.Criar(tenantId, unidadeId);
        await avisos.GravarAsync(aviso, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        await avisos.EnviarAsync(aviso, cancellationToken).ConfigureAwait(false);
        return MidiaTravada.ParaDto(midia);
    }
}
