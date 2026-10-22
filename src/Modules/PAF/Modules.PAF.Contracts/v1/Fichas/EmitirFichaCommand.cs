using FSH.Modules.PAF.Contracts.v1.Midias;
using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Fichas;

/// <summary>Body of the totem emission: the station and the idempotency key come from the credential and the header.</summary>
public sealed record EmitirFichaRequest(Guid FilaId, Guid PrioridadeId);

/// <summary>
/// Emission at the totem (RF11, seção 4.1). <paramref name="ChaveIdempotencia"/> is the emission cycle key
/// (header <c>Idempotency-Key</c>); a resend with the same key returns the stored ficha, never a new number.
/// </summary>
public sealed record EmitirFichaCommand(Guid EstacaoId, Guid FilaId, Guid PrioridadeId, string ChaveIdempotencia)
    : ICommand<FichaEmitidaDto>;

/// <summary>
/// What the totem prints. <paramref name="IdentificadorPublico"/> is the opaque id for the QR;
/// <paramref name="EmitidaEm"/> is the PostgreSQL instant shown in America/Sao_Paulo. <paramref name="Titulo"/>,
/// <paramref name="Rodape"/> and <paramref name="ImprimirQr"/> are the RF06 ficha parameters of the unidade at emission
/// (empty title or footer = that line is not printed).
/// </summary>
public sealed record FichaEmitidaDto(
    Guid FichaId,
    Guid AtendimentoId,
    Guid IdentificadorPublico,
    string Senha,
    string Letra,
    int Numero,
    string Unidade,
    string Fila,
    string Prioridade,
    DateTimeOffset EmitidaEm,
    string Titulo,
    string Rodape,
    bool ImprimirQr)
{
    /// <summary>C19: signed URL of the unidade's RF06 logo, printed at the top; null = the system logo.</summary>
    public UrlLeituraMidiaDto? Logotipo { get; init; }
}
