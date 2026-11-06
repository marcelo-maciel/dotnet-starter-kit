using FSH.Modules.PAF.Contracts.v1.Contratos;
using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Unidades;

/// <summary>
/// A unidade of the client tenant as the Codefi operator sees it (RF23): every contract, newest start first, so he
/// writes plan and limit, which nobody on the client side edits.
/// </summary>
public sealed record UnidadeOperadorDto(
    Guid Id,
    string Nome,
    string? DocumentoFiscal,
    bool Ativa,
    bool Inicial,
    DateTimeOffset CriadoEm,
    IReadOnlyList<ContratoUnidadeDto> Contratos);

/// <summary>Every unidade of the tenant the request targets, with no scope join: the operator has no PAF scope there.</summary>
public sealed record ListUnidadesOperadorQuery : IQuery<IReadOnlyList<UnidadeOperadorDto>>;
