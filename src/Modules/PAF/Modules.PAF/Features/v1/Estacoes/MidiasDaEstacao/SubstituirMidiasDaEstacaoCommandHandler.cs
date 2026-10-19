using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.TempoReal;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estacoes.MidiasDaEstacao;

/// <summary>
/// Replaces a painel station's whole list (M06, RF04, RF21 amended): two replacements of the same station run one after
/// the other under an advisory lock, so the list ends as one request wrote it, never a mix. The lock is not a row lock on
/// the station, which the hub's connection marks (O13) also update. <c>ProgramacaoAlterada</c> goes to the unidade.
/// </summary>
public sealed class SubstituirMidiasDaEstacaoCommandHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor,
    AvisosTempoReal avisos)
    : ICommandHandler<SubstituirMidiasDaEstacaoCommand, MidiasDaEstacaoDto>
{
    public async ValueTask<MidiasDaEstacaoDto> Handle(SubstituirMidiasDaEstacaoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var unidadeId = unidadeCorrente.Exigir();
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var chave = $"paf-painel-midias:{tenantId}:{command.EstacaoId:N}";
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({chave}, 0))", cancellationToken).ConfigureAwait(false);

        ListaDoPainel.ExigirPapelPainel(await ListaDoPainel.TipoAsync(db, command.EstacaoId, cancellationToken).ConfigureAwait(false));
        await ListaDoPainel.ExigirMidiasExibiveisAsync(db, tenantId, unidadeId, command.Midias.Distinct().ToList(), cancellationToken).ConfigureAwait(false);

        await db.PainelMidias.Where(p => p.EstacaoId == command.EstacaoId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.PainelMidias.AddRange(command.Midias.Select((midiaId, ordem) => PainelMidia.Criar(unidadeId, command.EstacaoId, midiaId, ordem)));

        // Written before SaveChanges: a failure there (the composite FK) rolls the notice back with the list.
        var aviso = ProgramacaoAlteradaPaf.Criar(tenantId, unidadeId);
        await avisos.GravarAsync(aviso, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        await avisos.EnviarAsync(aviso, cancellationToken).ConfigureAwait(false);

        return await ListaDoPainel.LerAsync(db, command.EstacaoId, cancellationToken).ConfigureAwait(false);
    }
}
