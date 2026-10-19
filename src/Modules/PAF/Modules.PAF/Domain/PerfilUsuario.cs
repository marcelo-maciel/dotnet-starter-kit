namespace FSH.Modules.PAF.Domain;

/// <summary>
/// What the PAF adds to a kit user (RF08): the scope kind, the free-text job title (D15, not the role) and the
/// extension. Lives in the PAF schema so the Identity model gains no column (ADR 0008). Tenant scope, no unit
/// filter. A user without a row has the "unidades selecionadas" scope.
/// </summary>
public sealed class PerfilUsuario
{
    public const int FuncaoMaxLength = 120;
    public const int RamalMaxLength = 20;

    public Guid UsuarioId { get; private set; }

    /// <summary>Sees every unidade of the tenant, including the ones created later; keeps one principal row.</summary>
    public bool EscopoEmpresa { get; private set; }

    // Free-text job title (D15) and extension, edited by the user cadastro (C12).
    public string? Funcao { get; private set; }
    public string? Ramal { get; private set; }

    private PerfilUsuario() { }

    public static PerfilUsuario Criar(Guid usuarioId) => new() { UsuarioId = usuarioId };

    public void DefinirEscopoEmpresa() => EscopoEmpresa = true;

    public void DefinirEscopoUnidadesSelecionadas() => EscopoEmpresa = false;

    /// <summary>Blank is stored as null, so the screen never shows an empty title as a value.</summary>
    public void DefinirFuncaoERamal(string? funcao, string? ramal)
    {
        Funcao = string.IsNullOrWhiteSpace(funcao) ? null : funcao.Trim();
        Ramal = string.IsNullOrWhiteSpace(ramal) ? null : ramal.Trim();
    }
}
