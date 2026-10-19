using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Estatistica;

/// <summary>The ten report types of RF07, numbered as the client listed them (requisitos RF07).</summary>
public enum TipoEstatistica
{
    // Never valid in a query: kept so the ten types keep the numbers the client uses (1 to 10).
    Nenhum = 0,
    AtendimentosPorTempoDeEspera = 1,
    AtendimentosPorTempoDeAtendimento = 2,
    AtendimentosPorGuiche = 3,
    AtendimentosPorAtendente = 4,
    AtendimentosPorHorarioDePico = 5,
    AtendimentosNaoEfetuadosOuCancelados = 6,
    FichasGeradasPorHorarioDePico = 7,
    FichasEmitidasPorGuiche = 8,
    TotaisPorTipoDeAtendimento = 9,
    TotalDeAtendimentos = 10,
}

/// <summary>Grouping dimension of types 9 and 10 only (decided 24/08/2026): a filter, never a new type.</summary>
public enum AgrupamentoEstatistica
{
    Fila,
    Setor,
}

public enum TipoColunaEstatistica
{
    Texto,
    Inteiro,
    DuracaoSegundos,
    Percentual,
}

/// <summary><paramref name="Chave"/> is stable for copy and export; <paramref name="Titulo"/> is what the screen shows.</summary>
public sealed record ColunaEstatisticaDto(string Chave, string Titulo, TipoColunaEstatistica Tipo);

/// <summary>One cell: <paramref name="Texto"/> for a <see cref="TipoColunaEstatistica.Texto"/> column, <paramref name="Numero"/> for the others.</summary>
public sealed record CelulaEstatisticaDto(string? Texto, decimal? Numero);

/// <summary><paramref name="UnidadeId"/> null = a row that sums the whole consulted scope.</summary>
public sealed record LinhaEstatisticaDto(Guid? UnidadeId, IReadOnlyList<CelulaEstatisticaDto> Celulas);

/// <summary>
/// Tabular result shared by every type, so a new type adds rows and columns without a new contract. Each row
/// has one cell per column, in column order. Totais are computed over the grouped atendimentos (weighted),
/// never as a mean of the row means (RF07, common rules).
/// </summary>
public sealed record ResultadoEstatisticaDto(
    TipoEstatistica Tipo,
    IReadOnlyList<ColunaEstatisticaDto> Colunas,
    IReadOnlyList<LinhaEstatisticaDto> Linhas,
    IReadOnlyList<LinhaEstatisticaDto> Totais);

/// <summary>
/// RF07 filters, bound from the query string. The period is inclusive and applies to the data de operação.
/// Empty <see cref="Unidades"/> = the caller's whole effective scope; any unidade outside it is refused, never
/// dropped. Empty <see cref="Filas"/> or <see cref="Prioridades"/> = no restriction.
/// </summary>
public sealed class ConsultarEstatisticaQuery : IQuery<ResultadoEstatisticaDto>
{
    public TipoEstatistica? Tipo { get; init; }

    public DateOnly? DataInicial { get; init; }

    public DateOnly? DataFinal { get; init; }

    public AgrupamentoEstatistica? Agrupamento { get; init; }

    public Guid[]? Unidades { get; init; }

    public Guid[]? Filas { get; init; }

    public Guid[]? Prioridades { get; init; }

    /// <summary>RF07 option of type 3: list only the stations that had an atendimento in the period.</summary>
    public bool SomenteEstacoesComAtendimento { get; init; }
}
