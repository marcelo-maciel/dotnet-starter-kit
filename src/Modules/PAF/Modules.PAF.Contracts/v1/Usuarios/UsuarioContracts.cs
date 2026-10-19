using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Usuarios;

/// <summary>
/// A PAF user as the cadastro screen lists it (RF08): kit identity (name, e-mail login, active flag), the PAF profile
/// (free-text job title, extension) and the stored scope in its order, first = principal. <c>Papeis</c> holds only the
/// PAF product roles the user has (Administrador, Gerente, Atendente).
/// </summary>
public sealed record UsuarioPafDto(
    Guid Id,
    string Nome,
    string Sobrenome,
    string Email,
    bool Ativo,
    string? Funcao,
    string? Ramal,
    bool EscopoEmpresa,
    IReadOnlyList<Guid> Unidades,
    IReadOnlyList<string> Papeis);

/// <summary>Only the users whose whole scope fits in the caller's (RF08); the tenant admin sees every user.</summary>
public sealed record ListUsuariosPafQuery : IQuery<IReadOnlyList<UsuarioPafDto>>;

/// <summary>
/// Creates the kit user (login = e-mail, D14), gives him one PAF product role, the PAF profile and the ordered scope.
/// The scope follows the grant rules of <c>SetUsuarioUnidadesCommand</c>: first = principal, escopo empresa carries
/// only the principal and is granted only by the tenant admin.
/// </summary>
public sealed record CreateUsuarioPafCommand(
    string Nome,
    string Sobrenome,
    string Email,
    string Senha,
    string ConfirmacaoSenha,
    string? Funcao,
    string? Ramal,
    string Papel,
    IReadOnlyList<Guid> Unidades,
    bool EscopoEmpresa) : ICommand<Guid>, IEscritaDeCadastro;

/// <summary>Name (kit), job title and extension (PAF). Role, scope and password have their own operations.</summary>
public sealed record UpdateUsuarioPafCommand(Guid UsuarioId, string Nome, string Sobrenome, string? Funcao, string? Ramal)
    : ICommand<Guid>, IEscritaDeCadastro;

/// <summary>
/// RF22: deactivates the kit user and revokes every session of his in the same act. With
/// <c>CancelarAtendimentosEmCurso</c> his Chamado and EmAtendimento atendimentos are cancelled by the RF12 cancel;
/// without it they stay where they are, in the abandoned-atendimento path.
/// </summary>
public sealed record DesativarUsuarioPafCommand(Guid UsuarioId, bool CancelarAtendimentosEmCurso) : ICommand<Guid>, IEscritaDeCadastro;

/// <summary>
/// Copies the PAF product roles and the scope (kind, list and order) of <c>UsuarioOrigemId</c> onto <c>UsuarioId</c>.
/// Both users must fit in the caller's scope (RF08). A unidade the target loses works as in the scope edit, including
/// the optional cancel of his atendimentos there.
/// </summary>
public sealed record CopiarPerfilUsuarioPafCommand(Guid UsuarioId, Guid UsuarioOrigemId, bool CancelarAtendimentosEmCurso)
    : ICommand<Guid>, IEscritaDeCadastro;

/// <summary>An atendimento the user holds Chamado or EmAtendimento, in any unidade, for the deactivation and scope screens.</summary>
public sealed record AtendimentoEmCursoDoUsuarioDto(
    Guid AtendimentoId,
    Guid UnidadeId,
    string Senha,
    string Estado,
    Guid? EstacaoId);

public sealed record ListAtendimentosEmCursoDoUsuarioQuery(Guid UsuarioId) : IQuery<IReadOnlyList<AtendimentoEmCursoDoUsuarioDto>>;

/// <summary>
/// RF08 (C16): the administrator sets another user's password, under the kit's password policy and history, and every
/// session of that user is revoked. Same reach rule as the other user operations: his whole scope fits in the caller's.
/// </summary>
public sealed record DefinirSenhaUsuarioPafCommand(Guid UsuarioId, string Senha, string ConfirmacaoSenha) : ICommand<Guid>, IEscritaDeCadastro
{
    // The record's generated ToString would print the password into any log or exception message that formats it.
    public override string ToString() => $"{nameof(DefinirSenhaUsuarioPafCommand)} {{ UsuarioId = {UsuarioId} }}";
}
