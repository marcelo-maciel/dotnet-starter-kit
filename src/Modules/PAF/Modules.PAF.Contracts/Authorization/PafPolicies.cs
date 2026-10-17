namespace FSH.Modules.PAF.Contracts.Authorization;

/// <summary>
/// Políticas da credencial de estação. A estação não é usuário e não tem permissão gravada no banco, então
/// RequirePermission a recusaria em qualquer endpoint (arquitetura seção 5); cada política aceita um só papel.
/// </summary>
public static class PafPolicies
{
    public const string EstacaoEmissao = "PafEstacaoEmissao";
    public const string EstacaoPainel = "PafEstacaoPainel";
}

/// <summary>Claims próprias da credencial de estação, além de sub, nameidentifier, tenant e jti.</summary>
public static class PafClaims
{
    public const string Papel = "papel";
    public const string Estacao = "estacao";
    public const string Unidade = "unidade";
    public const string Vinculo = "vinculo";

    public const string PapelEmissao = "Emissao";
    public const string PapelPainel = "Painel";
}
