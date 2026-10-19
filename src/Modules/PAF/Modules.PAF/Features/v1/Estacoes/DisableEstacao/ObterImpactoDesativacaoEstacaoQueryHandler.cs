using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Features.v1.Filas;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estacoes.DisableEstacao;

/// <summary>
/// Read-only preview for the deactivation dialog (RF04): what is in progress on the station and which pairs would lose
/// their last guichê. It takes no lock, so the deactivation re-checks everything under its own.
/// </summary>
public sealed class ObterImpactoDesativacaoEstacaoQueryHandler(PafDbContext db)
    : IQueryHandler<ObterImpactoDesativacaoEstacaoQuery, ImpactoDesativacaoEstacaoDto>
{
    public async ValueTask<ImpactoDesativacaoEstacaoDto> Handle(ObterImpactoDesativacaoEstacaoQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Unit-filtered: a station of another unit is indistinguishable from a missing one.
        var existe = await db.Estacoes.AsNoTracking().AnyAsync(e => e.Id == query.Id, cancellationToken).ConfigureAwait(false);
        if (!existe)
        {
            throw new NotFoundException($"Estacao {query.Id} não encontrada.");
        }

        var ocupado = await db.SessoesEstacao.AsNoTracking()
            .AnyAsync(v => v.EstacaoId == query.Id && v.LiberadoEm == null, cancellationToken)
            .ConfigureAwait(false);
        var emCurso = await DesativacaoEstacao.EmCursoAsync(db, query.Id, cancellationToken).ConfigureAwait(false);
        var cobertas = await CoberturaGuiche.CombinacoesCobertasAsync(db, cancellationToken).ConfigureAwait(false);
        var semAEstacao = await CoberturaGuiche.CombinacoesCobertasSemAEstacaoAsync(db, query.Id, cancellationToken).ConfigureAwait(false);

        return new ImpactoDesativacaoEstacaoDto(
            query.Id, ocupado, emCurso, cobertas.ExceptBy(semAEstacao.Select(c => c.Id), c => c.Id).ToList());
    }
}
