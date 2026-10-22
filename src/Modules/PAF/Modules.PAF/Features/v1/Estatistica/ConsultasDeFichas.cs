using FSH.Modules.PAF.Contracts.v1.Estatistica;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estatistica;

/// <summary>
/// Population and table shape of types 7 and 8 (RF07): every ficha whose own data de operação is in the inclusive
/// period, with no exclusion (requisitos RF07, "Quem fica de fora: Nenhuma"). Fila and prioridade are the ones of
/// the emission, which never change, so a forwarded ficha counts once and under the fila it was issued for.
/// </summary>
internal static class FichasDoPeriodo
{
    public const string FusoSaoPaulo = "America/Sao_Paulo";

    public const string RotuloTotal = "Total";

    public const string RotuloSubtotal = "Subtotal";

    // A report is a heavy read on an operational table: bounded below the connection string's 30 s, so one slow
    // report never holds a pooled connection for the full ceiling.
    public static readonly TimeSpan PrazoConsulta = TimeSpan.FromSeconds(15);

    public static readonly ColunaEstatisticaDto ColunaUnidade = new("unidade", "Unidade", TipoColunaEstatistica.Texto);

    private static readonly string[] SoOFiltroDeUnidade = [PafQueryFilters.Unidade];

    public static IQueryable<Ficha> Consultar(PafDbContext db, FiltroEstatistica filtro)
    {
        db.Database.SetCommandTimeout(PrazoConsulta);
        var unidades = filtro.Unidades.ToArray();
        var filas = filtro.Filas.ToArray();
        var prioridades = filtro.Prioridades.ToArray();

        // Only the unidade filter is lifted: the scope already chose the unidades, the tenant filter stays.
        var fichas = db.Fichas.IgnoreQueryFilters(SoOFiltroDeUnidade).AsNoTracking()
            .Where(f => unidades.Contains(f.UnidadeId)
                && f.DataOperacional >= filtro.DataInicial
                && f.DataOperacional <= filtro.DataFinal);
        if (filas.Length > 0)
        {
            fichas = fichas.Where(f => filas.Contains(f.FilaId));
        }

        if (prioridades.Length > 0)
        {
            fichas = fichas.Where(f => prioridades.Contains(f.PrioridadeId));
        }

        return fichas;
    }

    public static IQueryable<Estacao> Estacoes(PafDbContext db) =>
        db.Estacoes.IgnoreQueryFilters(SoOFiltroDeUnidade).AsNoTracking();

    public static async Task<Dictionary<Guid, string>> NomesDasUnidadesAsync(PafDbContext db, FiltroEstatistica filtro, CancellationToken ct)
    {
        var unidades = filtro.Unidades.ToArray();
        return await db.Unidades.AsNoTracking()
            .Where(u => unidades.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Nome, ct)
            .ConfigureAwait(false);
    }

    public static string NomeDaUnidade(Dictionary<Guid, string> nomes, Guid unidadeId) =>
        nomes.TryGetValue(unidadeId, out var nome) ? nome : unidadeId.ToString();

    public static CelulaEstatisticaDto Texto(string texto) => new(texto, null);

    public static CelulaEstatisticaDto Numero(int numero) => new(null, numero);

    /// <summary>
    /// Every row leads with the unidade and carries it; then one subtotal per unidade and the total of the whole
    /// consulted scope (<c>UnidadeId</c> null), always last. <paramref name="linhas"/> come already ordered.
    /// <paramref name="vazias"/> fills the text columns between the label and the count in subtotal and total.
    /// </summary>
    public static ResultadoEstatisticaDto Tabela(
        TipoEstatistica tipo,
        IReadOnlyList<ColunaEstatisticaDto> colunas,
        IReadOnlyList<(Guid UnidadeId, IReadOnlyList<string> Rotulos, int Fichas)> linhas,
        Dictionary<Guid, string> unidades,
        int vazias)
    {
        var corpo = linhas
            .Select(l => new LinhaEstatisticaDto(l.UnidadeId,
                [Texto(NomeDaUnidade(unidades, l.UnidadeId)), .. l.Rotulos.Select(Texto), Numero(l.Fichas)]))
            .ToList();
        var totais = linhas
            .GroupBy(l => l.UnidadeId)
            .OrderBy(g => NomeDaUnidade(unidades, g.Key), StringComparer.CurrentCulture)
            .Select(g => Resumo(g.Key, NomeDaUnidade(unidades, g.Key), RotuloSubtotal, vazias, g.Sum(l => l.Fichas)))
            .ToList();
        totais.Add(Resumo(null, RotuloTotal, string.Empty, vazias, linhas.Sum(l => l.Fichas)));
        return new ResultadoEstatisticaDto(tipo, colunas, corpo, totais);
    }

    private static LinhaEstatisticaDto Resumo(Guid? unidadeId, string primeiro, string segundo, int vazias, int fichas) =>
        new(unidadeId, [Texto(primeiro), Texto(segundo), .. Enumerable.Repeat(Texto(string.Empty), vazias), Numero(fichas)]);
}

/// <summary>
/// Type 7: fichas per hour of their creation in America/Sao_Paulo (as the legacy GetCountFichasGeradasPorHora),
/// the period still on the data de operação. Only hours with at least one ficha appear, as in the legacy grouping.
/// </summary>
public sealed class FichasGeradasPorHorarioDePico(PafDbContext db) : IConsultaEstatistica
{
    public static readonly ColunaEstatisticaDto ColunaHora = new("hora", "Hora", TipoColunaEstatistica.Texto);
    public static readonly ColunaEstatisticaDto ColunaFichas = new("fichas", "Fichas geradas", TipoColunaEstatistica.Inteiro);

    public TipoEstatistica Tipo => TipoEstatistica.FichasGeradasPorHorarioDePico;

    public static string RotuloDaHora(int hora) => $"{hora:00}h";

    public async Task<ResultadoEstatisticaDto> ConsultarAsync(FiltroEstatistica filtro, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        var horas = await FichasDoPeriodo.Consultar(db, filtro)
            // EF.Functions.AtTimeZone is the SQL Server provider's and fails to translate here; Npgsql translates this
            // conversion to "CriadoEm" AT TIME ZONE 'America/Sao_Paulo' (checked against PostgreSQL by the E2 tests).
            .GroupBy(f => new
            {
                f.UnidadeId,
                Hour = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(f.CriadoEm.UtcDateTime, FichasDoPeriodo.FusoSaoPaulo).Hour,
            })
            .Select(g => new { g.Key.UnidadeId, g.Key.Hour, Fichas = g.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var unidades = await FichasDoPeriodo.NomesDasUnidadesAsync(db, filtro, cancellationToken).ConfigureAwait(false);

        var linhas = horas
            .OrderBy(h => FichasDoPeriodo.NomeDaUnidade(unidades, h.UnidadeId), StringComparer.CurrentCulture)
            .ThenBy(h => h.UnidadeId)
            .ThenBy(h => h.Hour)
            .Select(h => (h.UnidadeId, (IReadOnlyList<string>)[RotuloDaHora(h.Hour)], h.Fichas))
            .ToList();
        return FichasDoPeriodo.Tabela(Tipo, [FichasDoPeriodo.ColunaUnidade, ColunaHora, ColunaFichas], linhas, unidades, vazias: 0);
    }
}

/// <summary>
/// Type 8: fichas per emitting station and channel, totem included (requisitos RF07: the population of types 7
/// and 8 has no exclusion). A deactivated station keeps its rows: the fichas it issued still happened.
/// </summary>
public sealed class FichasEmitidasPorGuiche(PafDbContext db) : IConsultaEstatistica
{
    public const string CanalGuiche = "Guichê";

    public const string CanalTotem = "Totem";

    public static readonly ColunaEstatisticaDto ColunaEstacao = new("estacao", "Estação", TipoColunaEstatistica.Texto);
    public static readonly ColunaEstatisticaDto ColunaCanal = new("canal", "Canal", TipoColunaEstatistica.Texto);
    public static readonly ColunaEstatisticaDto ColunaFichas = new("fichas", "Fichas emitidas", TipoColunaEstatistica.Inteiro);

    public TipoEstatistica Tipo => TipoEstatistica.FichasEmitidasPorGuiche;

    public static string NomeDoCanal(CanalEmissao canal) => canal switch
    {
        CanalEmissao.Guiche => CanalGuiche,
        CanalEmissao.Totem => CanalTotem,
        _ => throw new ArgumentOutOfRangeException(nameof(canal), canal, "Canal de emissão sem nome na estatística."),
    };

    public async Task<ResultadoEstatisticaDto> ConsultarAsync(FiltroEstatistica filtro, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        var contagens = await FichasDoPeriodo.Consultar(db, filtro)
            .GroupBy(f => new { f.UnidadeId, f.EstacaoId, f.Canal })
            .Select(g => new { g.Key.UnidadeId, g.Key.EstacaoId, g.Key.Canal, Fichas = g.Count() })
            .Join(FichasDoPeriodo.Estacoes(db), c => c.EstacaoId, e => e.Id,
                (c, e) => new { c.UnidadeId, c.EstacaoId, Estacao = e.Nome, c.Canal, c.Fichas })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var unidades = await FichasDoPeriodo.NomesDasUnidadesAsync(db, filtro, cancellationToken).ConfigureAwait(false);

        var linhas = contagens
            .OrderBy(c => FichasDoPeriodo.NomeDaUnidade(unidades, c.UnidadeId), StringComparer.CurrentCulture)
            .ThenBy(c => c.UnidadeId)
            .ThenBy(c => c.Estacao, StringComparer.CurrentCulture)
            .ThenBy(c => c.EstacaoId)
            .ThenBy(c => c.Canal)
            .Select(c => (c.UnidadeId, (IReadOnlyList<string>)[c.Estacao, NomeDoCanal(c.Canal)], c.Fichas))
            .ToList();
        return FichasDoPeriodo.Tabela(
            Tipo, [FichasDoPeriodo.ColunaUnidade, ColunaEstacao, ColunaCanal, ColunaFichas], linhas, unidades, vazias: 1);
    }
}
