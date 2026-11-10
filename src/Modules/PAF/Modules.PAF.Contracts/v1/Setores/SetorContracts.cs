using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Setores;

/// <summary>Setor of the current unidade (RF02): a name that groups filas in the RF07 reports, nothing else.</summary>
public sealed record SetorDto(Guid Id, Guid UnidadeId, string Nome);

/// <summary>Created in the current unidade; the name is unique there (<c>UX_Setores_Nome</c>).</summary>
public sealed record CreateSetorCommand(string Nome) : ICommand<Guid>, IEscritaDeCadastro;

/// <summary>Rename only: a setor is never deleted, so the reports keep grouping the past by it.</summary>
public sealed record UpdateSetorCommand(Guid Id, string Nome) : ICommand<Guid>, IEscritaDeCadastro;

/// <summary>Setores of the current unidade only, by name.</summary>
public sealed record ListSetoresQuery : IQuery<IReadOnlyList<SetorDto>>;
