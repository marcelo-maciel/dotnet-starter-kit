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
}
