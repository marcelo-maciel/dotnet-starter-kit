using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.ProgramacoesMidia;

/// <summary>RF10 edit with the version read; the mídia may change, and D13 is checked again with its duration.</summary>
public sealed class UpdateProgramacaoMidiaCommandHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : ICommandHandler<UpdateProgramacaoMidiaCommand, ProgramacaoMidiaDto>
{
    public async ValueTask<ProgramacaoMidiaDto> Handle(UpdateProgramacaoMidiaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var unidadeId = unidadeCorrente.Exigir();
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var programacao = await ProgramacaoTravada.TravarAsync(db, tenantId, unidadeId, command.Id, cancellationToken).ConfigureAwait(false);
        ProgramacaoTravada.ExigirVersao(programacao, command.Versao);
        var midia = await ProgramacaoTravada.ExigirMidiaProgramavelAsync(db, tenantId, unidadeId, command.MidiaId, cancellationToken).ConfigureAwait(false);
        ProgramacaoTravada.ExigirFimAteAMeiaNoite(command.HoraInicial, midia, command.Repeticoes);
        programacao.Editar(
            midia.Id, command.Nome.Trim(), command.DataInicial, command.DataFinal, command.HoraInicial,
            command.Repeticoes, (DiasSemana)command.Dias);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ProgramacaoTravada.ParaDto(programacao, midia);
    }
}
