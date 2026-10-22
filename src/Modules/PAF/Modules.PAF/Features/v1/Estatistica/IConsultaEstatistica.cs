using FSH.Modules.PAF.Contracts.v1.Estatistica;

namespace FSH.Modules.PAF.Features.v1.Estatistica;

/// <summary>
/// Filters already checked against the caller's scope. <see cref="Unidades"/> is never empty unless the scope
/// is: an implementation reads exactly these unidades, with IgnoreQueryFilters([PafQueryFilters.Unidade]),
/// never the current unit.
/// </summary>
public sealed record FiltroEstatistica(
    TipoEstatistica Tipo,
    DateOnly DataInicial,
    DateOnly DataFinal,
    AgrupamentoEstatistica? Agrupamento,
    IReadOnlyList<Guid> Unidades,
    IReadOnlyList<Guid> Filas,
    IReadOnlyList<Guid> Prioridades,
    bool SomenteEstacoesComAtendimento);

/// <summary>
/// One report type of RF07. Each type registers its own implementation in PafModule (E1, E2, E3), so tasks
/// running in parallel add a registration line instead of editing the shared handler.
/// </summary>
public interface IConsultaEstatistica
{
    TipoEstatistica Tipo { get; }

    Task<ResultadoEstatisticaDto> ConsultarAsync(FiltroEstatistica filtro, CancellationToken cancellationToken);
}
