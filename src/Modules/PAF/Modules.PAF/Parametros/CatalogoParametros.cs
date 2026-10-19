namespace FSH.Modules.PAF.Parametros;

/// <summary>
/// One RF06 parameter: the key stored in <c>Parametros.Chave</c>, the factory default used when no row exists
/// or the stored text is invalid, and the parse of the stored text.
/// </summary>
public sealed record DefinicaoParametro<T>(string Chave, T Padrao, Func<string, (bool Valido, T Valor)> Interpretar);

/// <summary>
/// Parameters known to the code. The table stores text; only keys listed here are ever read, so a typo in a
/// stored key is inert instead of silently changing behaviour.
/// </summary>
public static class CatalogoParametros
{
    /// <summary>
    /// RF06 Geral "chamar somente fichas da data atual" (requisitos seção 4, "Fichas não atendidas na virada do
    /// dia"): on by default; a client operating past midnight turns it off.
    /// </summary>
    public static readonly DefinicaoParametro<bool> SomenteFichasDaDataAtual =
        new("somenteFichasDaDataAtual", Padrao: true, InterpretarBooleano);

    public static (bool Valido, bool Valor) InterpretarBooleano(string valor) =>
        bool.TryParse(valor, out var resultado) ? (true, resultado) : (false, false);

    /// <summary>
    /// Floor of the totem font size in CSS pixels checked by the station cadastro (RNF19, D17 of 17/10/2026: 24 px,
    /// configurable). Read like any parameter, so changing the row changes the next validation without a build.
    /// </summary>
    public static readonly DefinicaoParametro<int> FonteMinimaTotemPx =
        new("fonteMinimaTotemPx", Padrao: 24, InterpretarInteiroPositivo);

    public static (bool Valido, int Valor) InterpretarInteiroPositivo(string valor) =>
        int.TryParse(valor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var resultado)
            && resultado > 0
            ? (true, resultado)
            : (false, 0);
}
