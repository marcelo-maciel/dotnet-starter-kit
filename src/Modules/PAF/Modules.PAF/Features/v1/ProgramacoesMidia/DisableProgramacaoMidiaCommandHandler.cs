using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.TempoReal;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.ProgramacoesMidia;

/// <summary>Turning off never depends on the mídia: a programação of a disabled mídia can always be switched off.</summary>
public sealed class DisableProgramacaoMidiaCommandHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor,
    AvisosTempoReal avisos)
    : ICommandHandler<DisableProgramacaoMidiaCommand, ProgramacaoMidiaDto>
{
    public async ValueTask<ProgramacaoMidiaDto> Handle(DisableProgramacaoMidiaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var unidadeId = unidadeCorrente.Exigir();
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var programacao = await ProgramacaoTravada.TravarAsync(db, tenantId, unidadeId, command.Id, cancellationToken).ConfigureAwait(false);
        programacao.Desativar();
        var aviso = ProgramacaoAlteradaPaf.Criar(tenantId, unidadeId);
        await avisos.GravarAsync(aviso, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var midia = await ProgramacaoTravada.LerMidiaAsync(db, programacao.MidiaId, cancellationToken).ConfigureAwait(false);
        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        await avisos.EnviarAsync(aviso, cancellationToken).ConfigureAwait(false);
        return ProgramacaoTravada.ParaDto(programacao, midia);
    }
}
