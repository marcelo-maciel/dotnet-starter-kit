using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Unidades;

/// <summary><c>Contrato</c> is the contract in force today, read-only; null when the unidade has none (no licence balance).</summary>
public sealed record UnidadeDto(
    Guid Id,
    string Nome,
    string? DocumentoFiscal,
    bool Ativa,
    DateTimeOffset CriadoEm,
    ContratoVigenteResumoDto? Contrato);

/// <summary>Plan and limit are written by the Codefi only (RF23): the client sees them, never edits them.</summary>
public sealed record ContratoVigenteResumoDto(string Plano, int LimiteSessoes, DateOnly? FimVigencia);

public sealed record CreateUnidadeCommand(string Nome, string? DocumentoFiscal) : ICommand<Guid>;

public sealed record UpdateUnidadeCommand(Guid Id, string Nome, string? DocumentoFiscal) : ICommand<Guid>;

public sealed record EnableUnidadeCommand(Guid Id) : ICommand<Guid>;

public sealed record DisableUnidadeCommand(Guid Id) : ICommand<Guid>;

public sealed record ListUnidadesQuery : IQuery<IReadOnlyList<UnidadeDto>>;

/// <summary>Whether the caller may create, disable and enable unidades (RF23): what the screen uses to show those buttons.</summary>
public sealed record AdministracaoUnidadesDto(bool AdministraUnidades);

public sealed record ObterAdministracaoUnidadesQuery : IQuery<AdministracaoUnidadesDto>;
