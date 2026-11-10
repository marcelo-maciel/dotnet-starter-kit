namespace FSH.Modules.PAF.Contracts.Authorization;

/// <summary>
/// Claims carried by the station credential (totem and painel are not users, ADR 0008 "Token da
/// estação"). The tenant travels in the kit's own <c>tenant</c> claim, which is what decides the
/// tenant of an authenticated request. A person never carries <see cref="Unidade"/>: their current
/// unit is session state on the server (ADR 0006).
/// </summary>
public static class PafClaims
{
    public const string Papel = "paf_papel";
    public const string Estacao = "paf_estacao";
    public const string Unidade = "paf_unidade";
    public const string Vinculo = "paf_vinculo";
}

/// <summary>
/// Values of <see cref="PafClaims.Papel"/>. Only the roles that run on a station credential: the
/// guichê always runs on the person's session (RF04, "uma credencial por papel").
/// </summary>
public static class PafPapeisEstacao
{
    public const string Emissao = "Emissao";
    public const string Painel = "Painel";
}
