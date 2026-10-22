using System.Globalization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Parametros;
using FSH.Modules.PAF.Sessao;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ObterAguardandoGuiche;

/// <summary>Row of the count, materialized by EF from raw SQL.</summary>
internal sealed record AguardandoNaFila(Guid FilaId, int Quantidade);

/// <summary>
/// The guichê's waiting counts (D30, legacy <c>Plugins/ETab.AE.Guiche/Plugin.cs:101</c>). Counted with the call's own
/// eligibility predicate, so the number is exactly what chamar-próximo can still reach: a ficha of yesterday with
/// "somente fichas da data atual" on, a prioridade the station does not enable or a fila outside its guichê role never
/// counts. A plain read, no lock: the number may be one call old by the time it is shown.
/// </summary>
public sealed class ObterAguardandoGuicheQueryHandler(PafDbContext db, SessaoCorrente sessao, ConcessaoCorrente concessao, LeitorParametros parametros)
    : IQueryHandler<ObterAguardandoGuicheQuery, AguardandoGuicheDto>
{
    public async ValueTask<AguardandoGuicheDto> Handle(ObterAguardandoGuicheQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var guiche = await OperacaoGuiche.ExigirAsync(db, DonoPosto.Exigir(sessao, concessao), cancellationToken);
        var lidos = await parametros.CarregarAsync(guiche.Estacao.UnidadeId, cancellationToken);
        var somenteDataAtual = lidos.Ler(CatalogoParametros.SomenteFichasDaDataAtual);

        var contagens = await db.Database.SqlQuery<AguardandoNaFila>(ConsultaAguardando(
            guiche.TenantId, guiche.Estacao.UnidadeId, guiche.Estacao.Id, somenteDataAtual)).ToListAsync(cancellationToken);
        var total = contagens.Sum(c => c.Quantidade);
        if (!lidos.Ler(CatalogoParametros.ExibirInformacaoDetalhadaFila))
        {
            return new AguardandoGuicheDto(total, PorFila: null);
        }

        var filas = await (
                from ef in db.EstacaoFilas.AsNoTracking()
                join f in db.Filas.AsNoTracking() on ef.FilaId equals f.Id
                where ef.EstacaoId == guiche.Estacao.Id && ef.Papel == PapelEstacao.Guiche
                orderby ef.Ordem, f.NomeExibicao
                select new { f.Id, f.NomeExibicao })
            .ToListAsync(cancellationToken);

        // Same RF06 "ordenação de filas" the totem buttons follow; by name keeps a stable pt-BR, case-insensitive order.
        var ordenadas = lidos.Ler(CatalogoParametros.OrdenacaoFilas) == CatalogoParametros.OrdenacaoPorNome
            ? filas.OrderBy(f => f.NomeExibicao, StringComparer.Create(CultureInfo.GetCultureInfo("pt-BR"), CompareOptions.IgnoreCase)).ToList()
            : filas;
        var porFila = contagens.ToDictionary(c => c.FilaId, c => c.Quantidade);
        return new AguardandoGuicheDto(
            total,
            ordenadas.Select(f => new AguardandoFilaDto(f.Id, f.NomeExibicao, porFila.GetValueOrDefault(f.Id))).ToList());
    }

    internal static FormattableString ConsultaAguardando(string tenantId, Guid unidadeId, Guid estacaoId, bool somenteDataAtual) =>
        ChamadaDeFicha.Concatenar(
            $"""
            SELECT a."FilaId", count(*)::int AS "Quantidade"
            """,
            ChamadaDeFicha.Elegiveis(tenantId, unidadeId, estacaoId, somenteDataAtual),
            $"""
             GROUP BY a."FilaId"
            """);
}
