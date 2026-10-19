using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.UsuariosUnidade;

/// <summary>Ordem 0 is the unidade principal, where the licence cascade starts (ADR 0006).</summary>
public sealed record UsuarioUnidadeDto(Guid UnidadeId, int Ordem);

/// <summary>
/// A user's stored scope (RF08). With <c>EscopoEmpresa</c> the user sees every unidade of the tenant and
/// <c>Unidades</c> holds only the principal.
/// </summary>
public sealed record EscopoUsuarioDto(bool EscopoEmpresa, IReadOnlyList<UsuarioUnidadeDto> Unidades);

public sealed record GetUsuarioUnidadesQuery(Guid UsuarioId) : IQuery<EscopoUsuarioDto>;

/// <summary>
/// Replaces the user's scope; the list order is the persisted order, first = principal. With
/// <c>EscopoEmpresa</c> the list carries exactly one unidade, the principal.
/// </summary>
public sealed record SetUsuarioUnidadesCommand(Guid UsuarioId, IReadOnlyList<Guid> Unidades, bool EscopoEmpresa) : ICommand<Guid>, IEscritaDeCadastro
{
    /// <summary>
    /// RF08 (C12): cancels, by the RF12 cancel and in the same transaction, the user's Chamado and EmAtendimento
    /// atendimentos in the unidades this edit takes out of his scope. False leaves them where they are.
    /// </summary>
    public bool CancelarAtendimentosEmCurso { get; init; }
}
