using FSH.Modules.PAF.Contracts.v1.Filas;
using FSH.Modules.PAF.Contracts.v1.Guiches;
using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Estacoes;

/// <summary>
/// Result of the deactivation (RF04). <paramref name="EmCurso"/> is what is still Chamado or EmAtendimento on the station
/// after the write (it keeps the guichê until it ends); <paramref name="GuicheLiberado"/> says a guichê link was released
/// in this act; <paramref name="CombinacoesSemCobertura"/> are the pairs the write left without any guichê (RF02).
/// </summary>
public sealed record EstacaoDesativadaDto(
    Guid Id,
    IReadOnlyList<CombinacaoFilaPrioridadeDto> CombinacoesSemCobertura,
    IReadOnlyList<AtendimentoEmCursoDto> EmCurso,
    bool GuicheLiberado);

/// <summary>What deactivating the station would do, read before the dialog confirms it (RF04, decided 03/10/2026).</summary>
public sealed record ObterImpactoDesativacaoEstacaoQuery(Guid Id) : IQuery<ImpactoDesativacaoEstacaoDto>;

/// <summary>
/// <paramref name="GuicheOcupado"/>: a session or grant holds the guichê now. <paramref name="CombinacoesSemCobertura"/>:
/// pairs covered now that no other active guichê covers, so they would leave the emission screens.
/// </summary>
public sealed record ImpactoDesativacaoEstacaoDto(
    Guid Id,
    bool GuicheOcupado,
    IReadOnlyList<AtendimentoEmCursoDto> EmCurso,
    IReadOnlyList<CombinacaoFilaPrioridadeDto> CombinacoesSemCobertura);
