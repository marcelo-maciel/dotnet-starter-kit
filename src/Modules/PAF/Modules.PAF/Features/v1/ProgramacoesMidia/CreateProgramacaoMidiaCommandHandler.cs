using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.ProgramacoesMidia;

/// <summary>
/// RF10 create. Overlap with another programação is accepted: D13 resolves it on air (latest start wins until it ends).
/// </summary>
public sealed class CreateProgramacaoMidiaCommandHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : ICommandHandler<CreateProgramacaoMidiaCommand, ProgramacaoMidiaDto>
{
    public async ValueTask<ProgramacaoMidiaDto> Handle(CreateProgramacaoMidiaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var unidadeId = unidadeCorrente.Exigir();
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var midia = await ProgramacaoTravada.ExigirMidiaProgramavelAsync(db, tenantId, unidadeId, command.MidiaId, cancellationToken).ConfigureAwait(false);
        ProgramacaoTravada.ExigirFimAteAMeiaNoite(command.HoraInicial, midia, command.Repeticoes);
        var programacao = ProgramacaoMidia.Criar(
            unidadeId, midia.Id, command.Nome.Trim(), command.DataInicial, command.DataFinal, command.HoraInicial,
            command.Repeticoes, (DiasSemana)command.Dias);
        db.ProgramacoesMidia.Add(programacao);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ProgramacaoTravada.ParaDto(programacao, midia);
    }
}
