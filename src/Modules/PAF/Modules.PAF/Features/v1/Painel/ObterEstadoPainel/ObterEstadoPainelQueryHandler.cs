using FSH.Modules.PAF.Contracts.v1.Painel;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.Features.v1.Estacoes;
using FSH.Modules.PAF.Parametros;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Painel.ObterEstadoPainel;

/// <summary>
/// The painel's re-query on connect and reconnect (RNF03, seção 7): today's calls of its filas, ordered by the
/// latest call or recall, each with its Versao so the screen drops older events. Real time is a notice; this is the truth.
/// </summary>
public sealed class ObterEstadoPainelQueryHandler(PafDbContext db, LeitorParametros parametros, IUnidadeCorrente unidadeCorrente)
    : IQueryHandler<ObterEstadoPainelQuery, EstadoPainelDto>
{
    public async ValueTask<EstadoPainelDto> Handle(ObterEstadoPainelQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var hoje = await PafRelogio.HojeAsync(db, cancellationToken);
        // RF06 "Painel: quantidade de fichas chamadas a exibir", of the painel's own unidade (set by its credential).
        var quantidade = await parametros.LerAsync(CatalogoParametros.QuantidadeChamadasPainel, unidadeCorrente.Exigir(), cancellationToken);

        var filasDoPainel = db.EstacaoFilas.AsNoTracking()
            .Where(ef => ef.EstacaoId == query.EstacaoId && ef.Papel == PapelEstacao.Painel)
            .Select(ef => ef.FilaId);

        var linhas = await (
                from a in db.Atendimentos.AsNoTracking()
                join f in db.Fichas.AsNoTracking() on a.FichaId equals f.Id
                join e in db.Estacoes.AsNoTracking() on a.EstacaoId equals e.Id
                where filasDoPainel.Contains(a.FilaId) && a.ChamadoEm != null && a.DataElegibilidade == hoje
                orderby (a.UltimaRechamadaEm ?? a.ChamadoEm) descending
                select new { a.Id, a.FichaId, a.Versao, f.Letra, f.Numero, a.Estado, EstacaoId = e.Id, e.Nome, e.Configuracao, a.ChamadoEm, a.UltimaRechamadaEm })
            .Take(quantidade)
            .ToListAsync(cancellationToken);

        return new EstadoPainelDto(quantidade, linhas
            .Select(l =>
            {
                var (numeroGuiche, nomeExibicaoGuiche) = ConfiguracaoDaEstacao.IdentidadeDoGuiche(l.Configuracao);
                return new ChamadaPainelDto(l.Id, l.FichaId, l.Versao, OperacaoGuiche.Senha(l.Letra, l.Numero), l.Estado.ToString(),
                    l.EstacaoId, l.Nome, l.ChamadoEm!.Value, l.UltimaRechamadaEm, numeroGuiche, nomeExibicaoGuiche);
            })
            .ToList());
    }
}
