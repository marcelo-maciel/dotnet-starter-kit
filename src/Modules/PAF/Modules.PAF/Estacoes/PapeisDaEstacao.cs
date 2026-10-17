using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Domain;

namespace FSH.Modules.PAF.Estacoes;

/// <summary>
/// Papéis que operam pela credencial de estação. Guichê e administração operam pela sessão do usuário
/// (RF04), então os tipos Guiche e Administracao não têm papel aqui e não recebem link.
/// </summary>
internal static class PapeisDaEstacao
{
    public static IReadOnlyList<PapelEstacao> ComCredencial(TipoEstacao tipo) => tipo switch
    {
        TipoEstacao.Emissao or TipoEstacao.EmissaoGuiche => [PapelEstacao.Emissao],
        TipoEstacao.Painel or TipoEstacao.GuichePainel => [PapelEstacao.Painel],
        TipoEstacao.EmissaoPainel => [PapelEstacao.Emissao, PapelEstacao.Painel],
        _ => [],
    };

    public static string ValorDaClaim(PapelEstacao papel) => papel switch
    {
        PapelEstacao.Emissao => PafClaims.PapelEmissao,
        PapelEstacao.Painel => PafClaims.PapelPainel,
        _ => throw new ArgumentOutOfRangeException(nameof(papel), papel, "Papel sem credencial de estação."),
    };
}
