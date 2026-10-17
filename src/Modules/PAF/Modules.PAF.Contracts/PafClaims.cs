namespace FSH.Modules.PAF.Contracts;

/// <summary>
/// Claims of the station credential (arquitetura seção 5). A person never carries <see cref="Unidade"/>:
/// their current unit is session state on the server (ADR 0006).
/// </summary>
public static class PafClaims
{
    public const string Papel = "paf_papel";
    public const string Estacao = "paf_estacao";
    public const string Unidade = "paf_unidade";
}
