using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estacoes.ObterConfiguracao;

public sealed class ObterConfiguracaoEstacaoQueryHandler(PafDbContext db)
    : IQueryHandler<ObterConfiguracaoEstacaoQuery, ConfiguracaoEstacaoDto>
{
    public async ValueTask<ConfiguracaoEstacaoDto> Handle(ObterConfiguracaoEstacaoQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return await db.Estacoes
            .AsNoTracking()
            .Where(e => e.Id == query.EstacaoId)
            .Select(e => new ConfiguracaoEstacaoDto(e.Id, e.UnidadeId, e.Nome, e.Tipo.ToString(), e.Configuracao))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Estacao {query.EstacaoId} not found.");
    }
}
