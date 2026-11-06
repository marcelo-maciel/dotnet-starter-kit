using FSH.Modules.PAF.Contracts.v1.Estatistica;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estatistica.Atendimentos;

/// <summary>
/// Population and arithmetic shared by types 1 to 6 (RF07, fórmulas de 03/10/2026). Every query reads the
/// unidades of the filter with only the unit filter off, so the tenant filter still applies; the period is
/// inclusive on the atendimento's own <see cref="Atendimento.DataOperacional"/>, never on the ficha's.
/// </summary>
internal static class EstatisticaAtendimentos
{
    // A report is a heavy read on the operational tables: bounded below the connection string's 30 s, so one
    // slow report never holds a pooled connection for the full ceiling.
    public static readonly TimeSpan PrazoConsulta = TimeSpan.FromSeconds(15);

    public const string RotuloTotal = "Total";
    public const string RotuloSubtotal = "Subtotal";

    public static readonly ColunaEstatisticaDto ColunaUnidade = new("unidade", "Unidade", TipoColunaEstatistica.Texto);
    public static readonly ColunaEstatisticaDto ColunaFila = new("fila", "Fila", TipoColunaEstatistica.Texto);
    public static readonly ColunaEstatisticaDto ColunaTotal = new("totalAtendimentos", "Total de atendimentos", TipoColunaEstatistica.Inteiro);
    public static readonly ColunaEstatisticaDto ColunaEspera = new("tempoMedioEspera", "Tempo médio de espera", TipoColunaEstatistica.DuracaoSegundos);
    public static readonly ColunaEstatisticaDto ColunaAtendimento = new("tempoMedioAtendimento", "Tempo médio de atendimento", TipoColunaEstatistica.DuracaoSegundos);

    private static readonly string[] SoOFiltroDeUnidade = [PafQueryFilters.Unidade];

    /// <summary>Every atendimento of the filter's unidades, period, filas and prioridades, in any state.</summary>
    public static IQueryable<Atendimento> DoPeriodo(PafDbContext db, FiltroEstatistica filtro)
    {
        db.Database.SetCommandTimeout(PrazoConsulta);
        var unidades = filtro.Unidades.ToArray();
        var consulta = db.Atendimentos.IgnoreQueryFilters(SoOFiltroDeUnidade).AsNoTracking()
            .Where(a => unidades.Contains(a.UnidadeId)
                && a.DataOperacional >= filtro.DataInicial
                && a.DataOperacional <= filtro.DataFinal);

        if (filtro.Filas.Count > 0)
        {
            var filas = filtro.Filas.ToArray();
            consulta = consulta.Where(a => filas.Contains(a.FilaId));
        }

        if (filtro.Prioridades.Count > 0)
        {
            var prioridades = filtro.Prioridades.ToArray();
            consulta = consulta.Where(a => prioridades.Contains(a.PrioridadeId));
        }

        return consulta;
    }

    /// <summary>
    /// Atendimentos realizados: encerrados in the period. Cancelled, never called and in progress stay out of the
    /// means and of the count alike, so mean and denominator count the same population.
    /// </summary>
    public static IQueryable<Atendimento> Realizados(PafDbContext db, FiltroEstatistica filtro) =>
        DoPeriodo(db, filtro).Where(a => a.Estado == EstadoAtendimento.Encerrado);

    /// <summary>
    /// Sums and counts of the realised atendimentos per (unidade, fila, estação, usuário), never means: every row,
    /// subtotal and total divides afterwards from the same sums, which is what makes the totals weighted by the
    /// number of atendimentos. Wait = first call minus eligibility (a recall rewrites only UltimaRechamadaEm).
    /// </summary>
    // ponytail: one query serves types 1 to 4 and regroups in memory; the rows are bounded by fila x guichê x
    // atendente per unidade (hundreds). Group in SQL per type if a tenant ever makes that thousands.
    public static async Task<List<Medida>> MedirRealizadosAsync(PafDbContext db, FiltroEstatistica filtro, CancellationToken ct) =>
        await Realizados(db, filtro)
            .GroupBy(a => new { a.UnidadeId, a.FilaId, a.EstacaoId, a.UsuarioId })
            .Select(g => new Medida(
                g.Key.UnidadeId,
                g.Key.FilaId,
                g.Key.EstacaoId,
                g.Key.UsuarioId,
                g.Count(),
                g.Sum(a => (a.ChamadoEm!.Value - a.ElegivelDesde).TotalSeconds),
                g.Sum(a => (a.EncerradoEm!.Value - a.IniciadoEm!.Value).TotalSeconds)))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public static async Task<Dictionary<Guid, string>> NomesDasUnidadesAsync(PafDbContext db, FiltroEstatistica filtro, CancellationToken ct)
    {
        var unidades = filtro.Unidades.ToArray();
        return await db.Unidades.AsNoTracking()
            .Where(u => unidades.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Nome, ct)
            .ConfigureAwait(false);
    }

    public static async Task<Dictionary<Guid, string>> NomesDasFilasAsync(PafDbContext db, IEnumerable<Guid> filas, CancellationToken ct)
    {
        var ids = filas.Distinct().ToArray();
        return await db.Filas.IgnoreQueryFilters(SoOFiltroDeUnidade).AsNoTracking()
            .Where(f => ids.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, f => f.Nome, ct)
            .ConfigureAwait(false);
    }

    public static IQueryable<Estacao> EstacoesSemFiltroDeUnidade(PafDbContext db) =>
        db.Estacoes.IgnoreQueryFilters(SoOFiltroDeUnidade).AsNoTracking();

    public static decimal? Media(double soma, int quantidade) =>
        quantidade == 0 ? null : Math.Round((decimal)soma / quantidade, 2);

    public static CelulaEstatisticaDto Texto(string texto) => new(texto, null);

    public static CelulaEstatisticaDto Numero(decimal? numero) => new(null, numero);

    public static string NomeDaUnidade(Dictionary<Guid, string> nomes, Guid unidadeId) =>
        nomes.TryGetValue(unidadeId, out var nome) ? nome : unidadeId.ToString();

    /// <summary>
    /// Rows in the order given, then one subtotal per unidade with rows and one total over the whole consulted scope
    /// (<c>UnidadeId</c> null), each computed by <paramref name="metricas"/> over the group's own rows, so a mean in a
    /// subtotal or total is weighted by the atendimentos, never a mean of the row means. Every row leads with the
    /// unidade and the group label, as <paramref name="colunas"/> must.
    /// </summary>
    public static ResultadoEstatisticaDto Tabela<T>(
        TipoEstatistica tipo,
        IReadOnlyList<ColunaEstatisticaDto> colunas,
        IReadOnlyList<T> grupos,
        Func<T, Guid> unidadeDe,
        Func<T, string> rotuloDe,
        Dictionary<Guid, string> unidades,
        Func<IReadOnlyList<T>, IReadOnlyList<CelulaEstatisticaDto>> metricas)
    {
        var linhas = grupos
            .Select(g => new LinhaEstatisticaDto(unidadeDe(g), [Texto(NomeDaUnidade(unidades, unidadeDe(g))), Texto(rotuloDe(g)), .. metricas([g])]))
            .ToList();
        var totais = grupos
            .GroupBy(unidadeDe)
            .OrderBy(g => NomeDaUnidade(unidades, g.Key), StringComparer.CurrentCulture)
            .Select(g => new LinhaEstatisticaDto(g.Key, [Texto(NomeDaUnidade(unidades, g.Key)), Texto(RotuloSubtotal), .. metricas([.. g])]))
            .ToList();
        totais.Add(new LinhaEstatisticaDto(null, [Texto(RotuloTotal), Texto(string.Empty), .. metricas(grupos)]));
        return new ResultadoEstatisticaDto(tipo, colunas, linhas, totais);
    }

    internal sealed record Medida(
        Guid UnidadeId, Guid FilaId, Guid? EstacaoId, Guid? UsuarioId, int Quantidade, double SomaEspera, double SomaAtendimento);

    internal sealed record Agregado(int Quantidade, double SomaEspera, double SomaAtendimento)
    {
        public decimal? MediaEspera => Media(SomaEspera, Quantidade);

        public decimal? MediaAtendimento => Media(SomaAtendimento, Quantidade);

        public static Agregado De(IEnumerable<Medida> medidas)
        {
            var lista = medidas.ToList();
            return new Agregado(lista.Sum(m => m.Quantidade), lista.Sum(m => m.SomaEspera), lista.Sum(m => m.SomaAtendimento));
        }

        public static Agregado Somar(IEnumerable<Agregado> agregados)
        {
            var lista = agregados.ToList();
            return new Agregado(lista.Sum(a => a.Quantidade), lista.Sum(a => a.SomaEspera), lista.Sum(a => a.SomaAtendimento));
        }
    }
}
