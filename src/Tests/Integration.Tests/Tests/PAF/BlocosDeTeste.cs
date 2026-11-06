using FSH.Modules.PAF.Contracts.v1.Estacoes;

namespace Integration.Tests.Tests.PAF;

/// <summary>Minimal valid RF04 blocks per station type, so a test that is not about the configuration states only its own subject.</summary>
internal static class BlocosDeTeste
{
    public static BlocoGuicheDto BlocoGuiche(int numero) => new(numero, NomeExibicao: null, LadoSeta: null, RespeitarOrdemFilas: false, SelecionarFicha: false);

    public static BlocoEmissaoDto BlocoEmissao() => new(null, null, null, null, null, null, null);

    public static BlocoPainelDto BlocoPainel() => new(null, null, null, null, null, null, null, null);

    public static BlocosEstacaoDto Guiche(int numero) => new(null, BlocoGuiche(numero), null);

    public static BlocosEstacaoDto GuichePainel(int numero) => new(null, BlocoGuiche(numero), BlocoPainel());

    public static BlocosEstacaoDto EmissaoGuiche(int numero) => new(BlocoEmissao(), BlocoGuiche(numero), null);
}
