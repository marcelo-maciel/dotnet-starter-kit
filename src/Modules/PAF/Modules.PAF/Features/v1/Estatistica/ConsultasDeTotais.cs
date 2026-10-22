using FSH.Modules.PAF.Contracts.v1.Estatistica;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using Microsoft.EntityFrameworkCore;
using static FSH.Modules.PAF.Features.v1.Estatistica.Atendimentos.EstatisticaAtendimentos;

namespace FSH.Modules.PAF.Features.v1.Estatistica;

/// <summary>
/// Population and grouping shared by types 9 and 10 (RF07, colunas de 17/10/2026): atendimentos counted per
/// (unidade, fila, prioridade, estado), then regrouped by fila or by the fila's CURRENT setor, so moving a fila to
/// another setor regroups the past too (no snapshot of the setor on the atendimento). Filas without setor form the
/// "Sem setor" group of their own unidade, which takes part in totals and percentages like any other group.
/// </summary>
internal static class TotaisAgrupados
{
    public const string RotuloSemSetor = "Sem setor";

    public static readonly ColunaEstatisticaDto ColunaSetor = new("setor", "Setor", TipoColunaEstatistica.Texto);

    private static readonly string[] SoOFiltroDeUnidade = [PafQueryFilters.Unidade];

    /// <summary>
    /// Groups ordered by unidade name, then label; "Sem setor" closes its unidade's setores. Only groups with at
    /// least one atendimento in the population appear.
    /// </summary>
    public static async Task<List<Grupo>> AgruparAsync(
        PafDbContext db, IQueryable<Atendimento> populacao, AgrupamentoEstatistica agrupamento, Dictionary<Guid, string> unidades, CancellationToken ct)
    {
        // Both sides lift only the unit filter: the handler never sets the current unit, and a fail-closed filter
        // left on Filas would drop every row of the join.
        var contagens = await populacao
            .Join(db.Filas.IgnoreQueryFilters(SoOFiltroDeUnidade), a => a.FilaId, f => f.Id, (a, f) => new { a.UnidadeId, a.FilaId, f.SetorId, a.PrioridadeId, a.Estado })
            .GroupBy(x => new { x.UnidadeId, x.FilaId, x.SetorId, x.PrioridadeId, x.Estado })
            .Select(g => new Contagem(g.Key.UnidadeId, g.Key.FilaId, g.Key.SetorId, g.Key.PrioridadeId, g.Key.Estado, g.Count()))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return agrupamento == AgrupamentoEstatistica.Setor
            ? await PorSetorAsync(db, contagens, unidades, ct).ConfigureAwait(false)
            : await PorFilaAsync(db, contagens, unidades, ct).ConfigureAwait(false);
    }

    public static ColunaEstatisticaDto ColunaDoGrupo(AgrupamentoEstatistica agrupamento) =>
        agrupamento == AgrupamentoEstatistica.Setor ? ColunaSetor : ColunaFila;

    /// <summary>Share of <paramref name="parte"/> in <paramref name="divisor"/>, in percent; null with nothing to divide.</summary>
    public static decimal? Percentual(int parte, int divisor) =>
        divisor == 0 ? null : Math.Round(100m * parte / divisor, 2);

    public static AgrupamentoEstatistica AgrupamentoDe(FiltroEstatistica filtro) =>
        filtro.Agrupamento ?? throw new ArgumentException("Agrupamento ausente depois da validação.", nameof(filtro));

    private static async Task<List<Grupo>> PorFilaAsync(PafDbContext db, List<Contagem> contagens, Dictionary<Guid, string> unidades, CancellationToken ct)
    {
        var filas = await NomesDasFilasAsync(db, contagens.Select(c => c.FilaId), ct).ConfigureAwait(false);
        return [.. contagens
            .GroupBy(c => new { c.UnidadeId, c.FilaId })
            .Select(g => new Grupo(g.Key.UnidadeId, filas[g.Key.FilaId], SemSetor: false, [.. g]))
            .OrderBy(g => NomeDaUnidade(unidades, g.UnidadeId), StringComparer.CurrentCulture)
            .ThenBy(g => g.Rotulo, StringComparer.CurrentCulture)];
    }

    private static async Task<List<Grupo>> PorSetorAsync(PafDbContext db, List<Contagem> contagens, Dictionary<Guid, string> unidades, CancellationToken ct)
    {
        var ids = contagens.Where(c => c.SetorId is not null).Select(c => c.SetorId!.Value).Distinct().ToArray();
        var setores = await db.Setores.IgnoreQueryFilters(SoOFiltroDeUnidade).AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Nome, ct)
            .ConfigureAwait(false);

        // The unidade is part of the key: "Sem setor" of two unidades are two groups, never one.
        return [.. contagens
            .GroupBy(c => new { c.UnidadeId, c.SetorId })
            .Select(g => new Grupo(
                g.Key.UnidadeId,
                g.Key.SetorId is { } setorId ? setores[setorId] : RotuloSemSetor,
                SemSetor: g.Key.SetorId is null,
                [.. g]))
            .OrderBy(g => NomeDaUnidade(unidades, g.UnidadeId), StringComparer.CurrentCulture)
            .ThenBy(g => g.SemSetor)
            .ThenBy(g => g.Rotulo, StringComparer.CurrentCulture)];
    }

    internal sealed record Contagem(Guid UnidadeId, Guid FilaId, Guid? SetorId, Guid PrioridadeId, EstadoAtendimento Estado, int Quantidade);

    internal sealed record Grupo(Guid UnidadeId, string Rotulo, bool SemSetor, IReadOnlyList<Contagem> Contagens);
}

/// <summary>
/// Type 9 (RF07, D07): realised atendimentos by prioridade, the V2 reading of the legacy "Prioritário / Normal".
/// The legacy had exactly those two; V2 lets each unidade register any number (RF05), so there is one count and one
/// percentage column per prioridade NAME of the consulted unidades (same name in two unidades = one column), ordered
/// by precedence (highest value first). Prioridade % is over the row's own total; the Total % is over the scope total.
/// </summary>
public sealed class ConsultaTotaisPorTipoDeAtendimento(PafDbContext db) : IConsultaEstatistica
{
    public static readonly ColunaEstatisticaDto ColunaTotal = new("totalAtendimentos", "Total", TipoColunaEstatistica.Inteiro);
    public static readonly ColunaEstatisticaDto ColunaTotalPercentual = new("totalAtendimentosPercentual", "%", TipoColunaEstatistica.Percentual);

    private static readonly string[] SoOFiltroDeUnidade = [PafQueryFilters.Unidade];

    public TipoEstatistica Tipo => TipoEstatistica.TotaisPorTipoDeAtendimento;

    public async Task<ResultadoEstatisticaDto> ConsultarAsync(FiltroEstatistica filtro, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        var agrupamento = TotaisAgrupados.AgrupamentoDe(filtro);
        var unidades = await NomesDasUnidadesAsync(db, filtro, cancellationToken).ConfigureAwait(false);
        var grupos = await TotaisAgrupados.AgruparAsync(db, Realizados(db, filtro), agrupamento, unidades, cancellationToken).ConfigureAwait(false);
        var prioridades = await PrioridadesAsync(filtro, grupos, cancellationToken).ConfigureAwait(false);
        var nomes = prioridades.Select(p => p.Nome).Distinct(StringComparer.Ordinal).ToList();
        var nomeDe = prioridades.ToDictionary(p => p.Id, p => p.Nome);
        var totalGeral = grupos.Sum(g => g.Contagens.Sum(c => c.Quantidade));

        List<ColunaEstatisticaDto> colunas = [ColunaUnidade, TotaisAgrupados.ColunaDoGrupo(agrupamento)];
        foreach (var nome in nomes)
        {
            colunas.Add(new ColunaEstatisticaDto($"prioridade:{nome}", nome, TipoColunaEstatistica.Inteiro));
            colunas.Add(new ColunaEstatisticaDto($"prioridade:{nome}:percentual", "%", TipoColunaEstatistica.Percentual));
        }

        colunas.Add(ColunaTotal);
        colunas.Add(ColunaTotalPercentual);

        return Tabela(Tipo, colunas, grupos, g => g.UnidadeId, g => g.Rotulo, unidades, conjunto =>
        {
            var contagens = conjunto.SelectMany(g => g.Contagens).ToList();
            var total = contagens.Sum(c => c.Quantidade);
            List<CelulaEstatisticaDto> celulas = [];
            foreach (var nome in nomes)
            {
                var quantidade = contagens.Where(c => nomeDe[c.PrioridadeId] == nome).Sum(c => c.Quantidade);
                celulas.Add(Numero(quantidade));
                celulas.Add(Numero(TotaisAgrupados.Percentual(quantidade, total)));
            }

            celulas.Add(Numero(total));
            celulas.Add(Numero(TotaisAgrupados.Percentual(total, totalGeral)));
            return celulas;
        });
    }

    /// <summary>
    /// Columns come from the cadastro, so a prioridade with no atendimento still shows its zero (a stable shape for
    /// copy and export): every active prioridade of the consulted unidades, narrowed by the prioridade filter, plus any
    /// deactivated one that has atendimentos in the result.
    /// </summary>
    private async Task<List<PrioridadeColuna>> PrioridadesAsync(FiltroEstatistica filtro, List<TotaisAgrupados.Grupo> grupos, CancellationToken ct)
    {
        var unidades = filtro.Unidades.ToArray();
        var filtradas = filtro.Prioridades.ToArray();
        var contadas = grupos.SelectMany(g => g.Contagens).Select(c => c.PrioridadeId).Distinct().ToArray();
        var lidas = await db.Prioridades.IgnoreQueryFilters(SoOFiltroDeUnidade).AsNoTracking()
            .Where(p => unidades.Contains(p.UnidadeId)
                && ((p.Ativa && (filtradas.Length == 0 || filtradas.Contains(p.Id))) || contadas.Contains(p.Id)))
            .Select(p => new PrioridadeColuna(p.Id, p.Nome, p.Valor))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // One name across unidades keeps the highest value of that name for the column order.
        var ordem = lidas.GroupBy(p => p.Nome, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Max(p => p.Valor), StringComparer.Ordinal);
        return [.. lidas.OrderByDescending(p => ordem[p.Nome]).ThenBy(p => p.Nome, StringComparer.CurrentCulture)];
    }

    private sealed record PrioridadeColuna(Guid Id, string Nome, int Valor);
}

/// <summary>
/// Type 10 (RF07, D07): the outcome of every atendimento of the period, in the four V2 states that partition it:
/// atendidos (encerrados), em atendimento, não efetuados (neither started nor cancelled: the legacy "desistências")
/// and cancelados (started or not), then the row's count of atendimentos. Every percentage is over the scope total of
/// atendimentos, so the "Total de atendimentos" percentages of the rows add up to 100.
/// </summary>
public sealed class ConsultaTotalDeAtendimentos(PafDbContext db) : IConsultaEstatistica
{
    public static readonly ColunaEstatisticaDto ColunaAtendidos = new("atendidos", "Atendidos", TipoColunaEstatistica.Inteiro);
    public static readonly ColunaEstatisticaDto ColunaEmAtendimento = new("emAtendimento", "Em atendimento", TipoColunaEstatistica.Inteiro);
    public static readonly ColunaEstatisticaDto ColunaNaoEfetuados = new("naoEfetuados", "Não efetuados", TipoColunaEstatistica.Inteiro);
    public static readonly ColunaEstatisticaDto ColunaCancelados = new("cancelados", "Cancelados", TipoColunaEstatistica.Inteiro);
    public static readonly ColunaEstatisticaDto ColunaTotalAtendimentos = new("totalAtendimentos", "Total de atendimentos", TipoColunaEstatistica.Inteiro);

    public TipoEstatistica Tipo => TipoEstatistica.TotalDeAtendimentos;

    public async Task<ResultadoEstatisticaDto> ConsultarAsync(FiltroEstatistica filtro, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        var agrupamento = TotaisAgrupados.AgrupamentoDe(filtro);
        var unidades = await NomesDasUnidadesAsync(db, filtro, cancellationToken).ConfigureAwait(false);
        var grupos = await TotaisAgrupados.AgruparAsync(db, DoPeriodo(db, filtro), agrupamento, unidades, cancellationToken).ConfigureAwait(false);
        var totalGeral = grupos.Sum(g => g.Contagens.Sum(c => c.Quantidade));

        ColunaEstatisticaDto[] contadas = [ColunaAtendidos, ColunaEmAtendimento, ColunaNaoEfetuados, ColunaCancelados, ColunaTotalAtendimentos];
        List<ColunaEstatisticaDto> colunas = [ColunaUnidade, TotaisAgrupados.ColunaDoGrupo(agrupamento)];
        foreach (var coluna in contadas)
        {
            colunas.Add(coluna);
            colunas.Add(new ColunaEstatisticaDto($"{coluna.Chave}Percentual", "%", TipoColunaEstatistica.Percentual));
        }

        return Tabela(Tipo, colunas, grupos, g => g.UnidadeId, g => g.Rotulo, unidades, conjunto =>
        {
            var contagens = conjunto.SelectMany(g => g.Contagens).ToList();
            int Contar(Func<EstadoAtendimento, bool> estado) => contagens.Where(c => estado(c.Estado)).Sum(c => c.Quantidade);
            int[] valores =
            [
                Contar(e => e == EstadoAtendimento.Encerrado),
                Contar(e => e == EstadoAtendimento.EmAtendimento),
                Contar(e => e is EstadoAtendimento.Aguardando or EstadoAtendimento.Chamado),
                Contar(e => e == EstadoAtendimento.Cancelado),
                contagens.Sum(c => c.Quantidade),
            ];
            return [.. valores.SelectMany(v => new[] { Numero(v), Numero(TotaisAgrupados.Percentual(v, totalGeral)) })];
        });
    }
}
