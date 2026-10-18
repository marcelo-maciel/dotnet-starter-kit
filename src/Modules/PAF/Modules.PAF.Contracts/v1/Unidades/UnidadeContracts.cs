using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Unidades;

public sealed record UnidadeDto(Guid Id, string Nome, string? DocumentoFiscal, bool Ativa, DateTimeOffset CriadoEm);

public sealed record CreateUnidadeCommand(string Nome, string? DocumentoFiscal) : ICommand<Guid>, IEscritaDeCadastro;

public sealed record UpdateUnidadeCommand(Guid Id, string Nome, string? DocumentoFiscal) : ICommand<Guid>, IEscritaDeCadastro;

public sealed record EnableUnidadeCommand(Guid Id) : ICommand<Guid>, IEscritaDeCadastro;

public sealed record DisableUnidadeCommand(Guid Id) : ICommand<Guid>, IEscritaDeCadastro;

public sealed record ListUnidadesQuery : IQuery<IReadOnlyList<UnidadeDto>>;
