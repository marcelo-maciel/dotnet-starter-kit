using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estacoes.ListEstacoes;

public sealed class ListEstacoesQueryHandler(PafDbContext db)
    : IQueryHandler<ListEstacoesQuery, IReadOnlyList<EstacaoDto>>
{
    public async ValueTask<IReadOnlyList<EstacaoDto>> Handle(ListEstacoesQuery query, CancellationToken cancellationToken)
    {
        var estacoes = await db.Estacoes.AsNoTracking()
            .OrderBy(e => e.Nome)
            .Select(e => new
            {
                e.Id,
                e.UnidadeId,
                e.Nome,
                e.Tipo,
                e.Ativa,
                e.Ramal,
                e.Configuracao,
                e.UltimaConexaoEmissaoEm,
                e.UltimaConexaoPainelEm,
                Filas = db.EstacaoFilas.Where(ef => ef.EstacaoId == e.Id).OrderBy(ef => ef.Ordem)
                    .Select(ef => new { ef.Papel, ef.FilaId, ef.Ordem }).ToList(),
                Prioridades = db.EstacaoPrioridades.Where(ep => ep.EstacaoId == e.Id).OrderBy(ep => ep.Ordem)
                    .Select(ep => new EstacaoPrioridadeItem(ep.PrioridadeId, ep.Ordem)).ToList(),
                // Generation revokes the previous link in the same transaction, so at most one is live; the newest
                // wins anyway, so a broken invariant shows the link the device would actually redeem.
                Vinculo = db.VinculosEstacao.Where(v => v.EstacaoId == e.Id && v.RevogadoEm == null)
                    .OrderByDescending(v => v.CriadoEm)
                    .Select(v => new
                    {
                        v.CriadoEm,
                        v.ResgatadoEm,
                        Emissao = v.CredencialEmissaoHash != null,
                        Painel = v.CredencialPainelHash != null,
                    })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return estacoes
            .Select(e => new EstacaoDto(
                e.Id, e.UnidadeId, e.Nome, e.Tipo.ToString(), e.Ativa,
                e.Filas.Select(f => new EstacaoFilaItem(f.Papel.ToString(), f.FilaId, f.Ordem)).ToList(),
                e.Prioridades,
                e.Ramal,
                ConfiguracaoDaEstacao.Ler(e.Configuracao),
                e.Vinculo is null
                    ? null
                    : new VinculoAtualDto(e.Vinculo.CriadoEm, e.Vinculo.ResgatadoEm, PapeisResgatados(e.Vinculo.Emissao, e.Vinculo.Painel)),
                e.UltimaConexaoEmissaoEm,
                e.UltimaConexaoPainelEm))
            .ToList();
    }

    private static List<string> PapeisResgatados(bool emissao, bool painel)
    {
        var papeis = new List<string>(2);
        if (emissao)
        {
            papeis.Add(PafPapeisEstacao.Emissao);
        }

        if (painel)
        {
            papeis.Add(PafPapeisEstacao.Painel);
        }

        return papeis;
    }
}
