namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// Sessão de pessoa já conferida pelo <see cref="SessionCheckMiddleware"/> nesta requisição. Vazia para
/// operador do tenant raiz sem licença, impersonação e estação: quem exige sessão PAF recusa nesse caso.
/// </summary>
public sealed class SessaoCorrente
{
    public string? TenantId { get; private set; }
    public Guid? SessaoId { get; private set; }
    public Guid? UsuarioId { get; private set; }

    /// <summary>Unidade que ocupa a licença; lida no início da requisição; a troca de unidade a migra (S07).</summary>
    public Guid? UnidadeLicencaId { get; private set; }

    public void Definir(string tenantId, Guid sessaoId, Guid usuarioId, Guid unidadeLicencaId)
    {
        TenantId = tenantId;
        SessaoId = sessaoId;
        UsuarioId = usuarioId;
        UnidadeLicencaId = unidadeLicencaId;
    }
}
