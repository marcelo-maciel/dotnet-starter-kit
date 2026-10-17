using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estacoes;

/// <summary>The filas per papel and the prioridades of a station (RF04), validated and written the same way on create and update.</summary>
internal static class ListasDaEstacao
{
    public static IReadOnlyList<PapelEstacao> Papeis(TipoEstacao tipo) => tipo switch
    {
        TipoEstacao.Administracao => [],
        TipoEstacao.Emissao => [PapelEstacao.Emissao],
        TipoEstacao.EmissaoGuiche => [PapelEstacao.Emissao, PapelEstacao.Guiche],
        TipoEstacao.EmissaoPainel => [PapelEstacao.Emissao, PapelEstacao.Painel],
        TipoEstacao.Guiche => [PapelEstacao.Guiche],
        TipoEstacao.GuichePainel => [PapelEstacao.Guiche, PapelEstacao.Painel],
        TipoEstacao.Painel => [PapelEstacao.Painel],
        _ => throw new ArgumentOutOfRangeException(nameof(tipo), tipo, null),
    };

    public static async Task ValidarAsync(
        PafDbContext db,
        TipoEstacao tipo,
        IReadOnlyList<EstacaoFilaItem> filas,
        IReadOnlyList<EstacaoPrioridadeItem> prioridades,
        CancellationToken cancellationToken)
    {
        var papeis = Papeis(tipo);
        var erros = new List<string>();

        var foraDoTipo = filas.Select(f => Enum.Parse<PapelEstacao>(f.Papel)).Where(p => !papeis.Contains(p)).Distinct().ToList();
        erros.AddRange(foraDoTipo.Select(p => $"O tipo {tipo} não tem o papel {p}."));

        // A guichê without a fila or a prioridade never finds a candidate and fails in silence (RF04).
        if (papeis.Contains(PapelEstacao.Guiche))
        {
            if (!filas.Any(f => Enum.Parse<PapelEstacao>(f.Papel) == PapelEstacao.Guiche))
            {
                erros.Add("A estação de guichê precisa de ao menos uma fila no papel Guiche.");
            }

            if (prioridades.Count == 0)
            {
                erros.Add("A estação de guichê precisa de ao menos uma prioridade habilitada.");
            }
        }

        if (erros.Count > 0)
        {
            throw new CustomException("Estação inválida.", erros, HttpStatusCode.BadRequest);
        }

        // Under the unit filter, a fila or prioridade of another unidade does not exist here (RF04).
        var filaIds = filas.Select(f => f.FilaId).Distinct().ToList();
        var prioridadeIds = prioridades.Select(p => p.PrioridadeId).Distinct().ToList();
        int filasDaUnidade = await db.Filas.CountAsync(f => filaIds.Contains(f.Id), cancellationToken).ConfigureAwait(false);
        int prioridadesDaUnidade = await db.Prioridades.CountAsync(p => prioridadeIds.Contains(p.Id), cancellationToken).ConfigureAwait(false);
        if (filasDaUnidade != filaIds.Count || prioridadesDaUnidade != prioridadeIds.Count)
        {
            throw new NotFoundException("Fila ou prioridade não encontrada nesta unidade.");
        }
    }

    /// <summary>Makes the stored lists equal the given ones: keeps, reorders, adds and removes join rows.</summary>
    public static async Task SincronizarAsync(
        PafDbContext db,
        Estacao estacao,
        IReadOnlyList<EstacaoFilaItem> filas,
        IReadOnlyList<EstacaoPrioridadeItem> prioridades,
        CancellationToken cancellationToken)
    {
        var filasAtuais = await db.EstacaoFilas.Where(ef => ef.EstacaoId == estacao.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var desejadas = filas.ToDictionary(f => (Enum.Parse<PapelEstacao>(f.Papel), f.FilaId), f => f.Ordem);
        foreach (var atual in filasAtuais)
        {
            if (desejadas.Remove((atual.Papel, atual.FilaId), out int ordem))
            {
                atual.DefinirOrdem(ordem);
            }
            else
            {
                db.EstacaoFilas.Remove(atual);
            }
        }

        db.EstacaoFilas.AddRange(desejadas.Select(d => EstacaoFila.Criar(estacao.UnidadeId, estacao.Id, d.Key.Item1, d.Key.Item2, d.Value)));

        var prioridadesAtuais = await db.EstacaoPrioridades.Where(ep => ep.EstacaoId == estacao.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var desejadasP = prioridades.ToDictionary(p => p.PrioridadeId, p => p.Ordem);
        foreach (var atual in prioridadesAtuais)
        {
            if (desejadasP.Remove(atual.PrioridadeId, out int ordem))
            {
                atual.DefinirOrdem(ordem);
            }
            else
            {
                db.EstacaoPrioridades.Remove(atual);
            }
        }

        db.EstacaoPrioridades.AddRange(desejadasP.Select(d => EstacaoPrioridade.Criar(estacao.UnidadeId, estacao.Id, d.Key, d.Value)));
    }
}
