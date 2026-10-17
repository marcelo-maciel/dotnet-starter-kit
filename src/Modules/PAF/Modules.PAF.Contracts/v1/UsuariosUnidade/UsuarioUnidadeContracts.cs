using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.UsuariosUnidade;

/// <summary>Ordem 0 is the unidade principal, where the licence cascade starts (ADR 0006).</summary>
public sealed record UsuarioUnidadeDto(Guid UnidadeId, int Ordem);

public sealed record GetUsuarioUnidadesQuery(Guid UsuarioId) : IQuery<IReadOnlyList<UsuarioUnidadeDto>>;

/// <summary>Replaces the user's scope; the list order is the persisted order, first = principal.</summary>
public sealed record SetUsuarioUnidadesCommand(Guid UsuarioId, IReadOnlyList<Guid> Unidades) : ICommand<Guid>;
