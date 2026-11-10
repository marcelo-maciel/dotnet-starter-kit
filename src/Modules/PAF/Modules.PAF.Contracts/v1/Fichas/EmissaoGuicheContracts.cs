using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Fichas;

/// <summary>Body of the guichê emission: the station comes from the session's guichê, the key from the header.</summary>
public sealed record EmitirFichaNoGuicheRequest(Guid FilaId, Guid PrioridadeId);

/// <summary>
/// Manual emission at the guichê (RF11): canal <c>Guiche</c>, emitting station = the guichê of the session's link,
/// numbered in the same sequence as the totem. <paramref name="ChaveIdempotencia"/> is the header
/// <c>Idempotency-Key</c>; a resend with the same key returns the stored ficha, never a new number.
/// </summary>
public sealed record EmitirFichaNoGuicheCommand(Guid FilaId, Guid PrioridadeId, string ChaveIdempotencia)
    : ICommand<FichaEmitidaDto>, IEscritaDeCadastro;

/// <summary>The guichê emission panel: the grid and the station's last ficha of today.</summary>
public sealed record ObterEmissaoGuicheQuery : IQuery<EmissaoGuicheDto>;

/// <summary>
/// <paramref name="UltimaFicha"/> is the last ficha this guichê emitted today (America/Sao_Paulo, database clock), as
/// printed now, so "reimprimir a última" prints it again without a new number; null when none.
/// </summary>
public sealed record EmissaoGuicheDto(IReadOnlyList<FilaEmissaoGuicheDto> Filas, FichaEmitidaDto? UltimaFicha);

/// <summary>
/// One grid line (RF11 l.364): every fila of the current unidade, inactive ones included with <paramref name="Ativa"/>
/// false. <paramref name="Emissivel"/> = unidade and fila active and the fila window open by the database clock;
/// only then the screen offers the buttons (RF02: outside the window the button disappears from the guichê too).
/// <paramref name="UltimaChamadaEm"/> is the fila's last call in America/Sao_Paulo, null when never called.
/// <paramref name="Prioridades"/> lists only active prioridades covered by a guichê (C07).
/// </summary>
public sealed record FilaEmissaoGuicheDto(
    Guid FilaId,
    string NomeExibicao,
    bool Ativa,
    bool ForaDoHorario,
    bool Emissivel,
    DateTimeOffset? UltimaChamadaEm,
    IReadOnlyList<PrioridadeEmissaoGuicheDto> Prioridades);

public sealed record PrioridadeEmissaoGuicheDto(Guid PrioridadeId, string Nome, string Letra);
