using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Atendimentos;

/// <summary>How many fichas the session's guichê could call now (RF06 "exibir informação detalhada de fila", D30).</summary>
public sealed record ObterAguardandoGuicheQuery : IQuery<AguardandoGuicheDto>;

/// <summary>
/// <paramref name="Total"/>: every waiting atendimento the guichê could call. <paramref name="PorFila"/>: one line per
/// fila of the station's guichê role, zero included, in the RF06 fila order; null when the parameter is off, so the
/// screen cannot show the detail the administrator turned off.
/// </summary>
public sealed record AguardandoGuicheDto(int Total, IReadOnlyList<AguardandoFilaDto>? PorFila);

public sealed record AguardandoFilaDto(Guid FilaId, string Fila, int Quantidade);
