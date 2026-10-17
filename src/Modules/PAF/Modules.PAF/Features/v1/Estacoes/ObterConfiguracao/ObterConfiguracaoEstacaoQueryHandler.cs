using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estacoes.ObterConfiguracao;

public sealed class ObterConfiguracaoEstacaoQueryHandler(PafDbContext db)
    : IQueryHandler<ObterConfiguracaoEstacaoQuery, ConfiguracaoEstacaoDto>
{
    public async ValueTask<ConfiguracaoEstacaoDto> Handle(ObterConfiguracaoEstacaoQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var estacao = await db.Estacoes
            .AsNoTracking()
            .Where(e => e.Id == query.EstacaoId)
            .Select(e => new { e.Id, e.UnidadeId, e.Nome, e.Tipo, e.Configuracao })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Estacao {query.EstacaoId} not found.");

        var botoes = await BotoesAsync(estacao.Id, cancellationToken).ConfigureAwait(false);
        return new ConfiguracaoEstacaoDto(estacao.Id, estacao.UnidadeId, estacao.Nome, estacao.Tipo.ToString(), estacao.Configuracao, botoes);
    }

    /// <summary>
    /// Same rule the emission enforces (fila of the station's emission role, fila and prioridade active), so a
    /// button never leads to a refusal. Ordered by the station's fila order, then the fila's prioridade order.
    /// </summary>
    private async Task<List<BotaoEmissaoDto>> BotoesAsync(Guid estacaoId, CancellationToken ct)
    {
        var linhas = await (
                from ef in db.EstacaoFilas.AsNoTracking()
                join f in db.Filas.AsNoTracking() on ef.FilaId equals f.Id
                join fp in db.FilaPrioridades.AsNoTracking() on f.Id equals fp.FilaId
                join p in db.Prioridades.AsNoTracking() on fp.PrioridadeId equals p.Id
                where ef.EstacaoId == estacaoId && ef.Papel == PapelEstacao.Emissao && f.Ativa && p.Ativa
                orderby ef.Ordem, fp.Ordem, f.NomeExibicao, p.Nome
                select new { f.Id, f.NomeExibicao, PrioridadeId = p.Id, p.Nome, fp.Letra })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // A flat position: the two source orders may tie across filas, and the screen sorts by one number.
        return linhas.Select((l, i) => new BotaoEmissaoDto(l.Id, l.NomeExibicao, l.PrioridadeId, l.Nome, l.Letra, i)).ToList();
    }
}
