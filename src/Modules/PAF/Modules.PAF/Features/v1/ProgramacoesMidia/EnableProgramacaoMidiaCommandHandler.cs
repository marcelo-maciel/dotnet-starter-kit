using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.ProgramacoesMidia;

/// <summary>
/// Turning a programação back on is a cadastro write like create: its mídia must still be programmable and it must
/// still end by midnight with the mídia's current duration, else 400 and it stays off.
/// </summary>
public sealed class EnableProgramacaoMidiaCommandHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : ICommandHandler<EnableProgramacaoMidiaCommand, ProgramacaoMidiaDto>
{
    public async ValueTask<ProgramacaoMidiaDto> Handle(EnableProgramacaoMidiaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var unidadeId = unidadeCorrente.Exigir();
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var programacao = await ProgramacaoTravada.TravarAsync(db, tenantId, unidadeId, command.Id, cancellationToken).ConfigureAwait(false);
        var midia = await ProgramacaoTravada.ExigirMidiaProgramavelAsync(db, tenantId, unidadeId, programacao.MidiaId, cancellationToken).ConfigureAwait(false);
        ProgramacaoTravada.ExigirFimAteAMeiaNoite(programacao.HoraInicial, midia, programacao.Repeticoes);
        programacao.Ativar();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ProgramacaoTravada.ParaDto(programacao, midia);
    }
}
