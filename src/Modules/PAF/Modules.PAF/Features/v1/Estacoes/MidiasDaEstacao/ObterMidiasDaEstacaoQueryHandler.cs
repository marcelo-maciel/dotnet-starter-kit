using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Estacoes.MidiasDaEstacao;

/// <summary>The stored list as the cadastro edits it, inactive mídias included (the painel skips them).</summary>
public sealed class ObterMidiasDaEstacaoQueryHandler(PafDbContext db) : IQueryHandler<ObterMidiasDaEstacaoQuery, MidiasDaEstacaoDto>
{
    public async ValueTask<MidiasDaEstacaoDto> Handle(ObterMidiasDaEstacaoQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await ListaDoPainel.TipoAsync(db, query.EstacaoId, cancellationToken).ConfigureAwait(false);
        return await ListaDoPainel.LerAsync(db, query.EstacaoId, cancellationToken).ConfigureAwait(false);
    }
}
