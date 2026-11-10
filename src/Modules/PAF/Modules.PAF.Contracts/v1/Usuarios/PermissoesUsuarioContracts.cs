using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Usuarios;

/// <summary>One PAF permission as the user screen shows it: the constant of <c>PafPermissions</c> and its label.</summary>
public sealed record PermissaoPafDto(string Nome, string Rotulo);

/// <summary>One of the four RF08 blocks (Controle de Fila, Atendimento, Painel Chamador, Administração).</summary>
public sealed record GrupoPermissoesPafDto(string Nome, string? Observacao, IReadOnlyList<PermissaoPafDto> Permissoes);

/// <summary>
/// RF08 per-user permissions (C15). <c>DosPapeis</c> come from the user's roles (the PAF product role, the kit's basic
/// role) and are the starting point the screen shows checked and locked; <c>Individuais</c> are the user's own extra
/// grants; <c>AlteraveisPeloChamador</c> are the permissions the caller holds, the only ones he may grant or take away.
/// </summary>
public sealed record PermissoesUsuarioPafDto(
    Guid UsuarioId,
    IReadOnlyList<GrupoPermissoesPafDto> Grupos,
    IReadOnlyList<string> DosPapeis,
    IReadOnlyList<string> Individuais,
    IReadOnlyList<string> AlteraveisPeloChamador);

/// <summary>Only for a user whose whole scope fits in the caller's (RF08).</summary>
public sealed record GetPermissoesUsuarioPafQuery(Guid UsuarioId) : IQuery<PermissoesUsuarioPafDto>;

/// <summary>
/// Replaces the user's individual PAF permissions with <c>Permissoes</c> (every one listed in the four groups). The
/// caller only adds or removes permissions he holds himself, and only on a user whose scope fits in his (RF08).
/// </summary>
public sealed record DefinirPermissoesUsuarioPafCommand(Guid UsuarioId, IReadOnlyList<string> Permissoes)
    : ICommand<Guid>, IEscritaDeCadastro;
