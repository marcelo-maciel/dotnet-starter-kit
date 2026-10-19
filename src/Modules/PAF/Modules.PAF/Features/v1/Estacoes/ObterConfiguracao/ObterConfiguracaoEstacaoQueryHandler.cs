using System.Globalization;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Filas;
using FSH.Modules.PAF.Parametros;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estacoes.ObterConfiguracao;

public sealed class ObterConfiguracaoEstacaoQueryHandler(PafDbContext db, LeitorParametros parametros)
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

        var botoes = await BotoesAsync(estacao.Id, estacao.UnidadeId, cancellationToken).ConfigureAwait(false);
        return new ConfiguracaoEstacaoDto(estacao.Id, estacao.UnidadeId, estacao.Nome, estacao.Tipo.ToString(), estacao.Configuracao, botoes);
    }

    /// <summary>
    /// Same rule the emission enforces (fila of the station's emission role, unidade, fila and prioridade active,
    /// inside the fila's window by the database clock, combination covered by a guichê), so a button never leads
    /// to a refusal at the moment it is read. Ordered by the station's fila order, then the fila's prioridade order.
    /// </summary>
    private async Task<List<BotaoEmissaoDto>> BotoesAsync(Guid estacaoId, Guid unidadeId, CancellationToken ct)
    {
        var unidadeAtiva = await db.Unidades.AsNoTracking().AnyAsync(u => u.Id == unidadeId && u.Ativa, ct).ConfigureAwait(false);
        if (!unidadeAtiva)
        {
            return [];
        }

        var cobertas = CoberturaGuiche.Pares(db);
        var linhas = await (
                from ef in db.EstacaoFilas.AsNoTracking()
                join f in db.Filas.AsNoTracking() on ef.FilaId equals f.Id
                join fp in db.FilaPrioridades.AsNoTracking() on f.Id equals fp.FilaId
                join p in db.Prioridades.AsNoTracking() on fp.PrioridadeId equals p.Id
                where ef.EstacaoId == estacaoId && ef.Papel == PapelEstacao.Emissao && f.Ativa && p.Ativa
                    && cobertas.Any(c => c.FilaId == f.Id && c.PrioridadeId == p.Id)
                orderby ef.Ordem, fp.Ordem, f.NomeExibicao, p.Nome
                select new
                {
                    f.Id, f.NomeExibicao, f.HoraInicial, f.HoraFinal, PrioridadeId = p.Id, p.Nome, fp.Letra,
                    f.Fonte, f.CorFonte, fp.CorBotao, f.CorBotaoSelecionado,
                })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ponytail: the window is applied when the totem reads; it re-reads every 60 s and on FilaAtualizada, and
        // the emission refuses outside the window anyway. A pushed notice at each window edge needs a scheduler.
        var hora = TimeOnly.FromDateTime(await PafRelogio.AgoraEmSaoPauloAsync(db, ct).ConfigureAwait(false));

        // RF06 "Ordenação de filas": by name keeps each fila's prioridade order (OrderBy is stable).
        var ordenacao = await parametros.LerAsync(CatalogoParametros.OrdenacaoFilas, unidadeId, ct).ConfigureAwait(false);
        var ordenadas = ordenacao == CatalogoParametros.OrdenacaoPorNome
            ? linhas.OrderBy(l => l.NomeExibicao, StringComparer.Create(CultureInfo.GetCultureInfo("pt-BR"), CompareOptions.IgnoreCase)).ToList()
            : linhas;

        // A flat position: the two source orders may tie across filas, and the screen sorts by one number.
        return ordenadas
            .Where(l => Fila.JanelaPermiteEmissao(l.HoraInicial, l.HoraFinal, hora))
            .Select((l, i) => new BotaoEmissaoDto(
                l.Id, l.NomeExibicao, l.PrioridadeId, l.Nome, l.Letra, i, l.Fonte, l.CorFonte, l.CorBotao, l.CorBotaoSelecionado))
            .ToList();
    }
}
