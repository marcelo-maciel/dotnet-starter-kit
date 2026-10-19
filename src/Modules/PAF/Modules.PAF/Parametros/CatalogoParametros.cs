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

    // --- C10: the rest of RF06 (requisitos l.224-235). Logotipo is C19; the other Geral keys are C20. ---

    public const int TamanhoMaximoTitulo = 60;
    public const int TamanhoMaximoRodape = 200;
    public const int QuantidadeMaximaChamadasPainel = 30;
    public const string OrdenacaoPorOrdem = "ordem";
    public const string OrdenacaoPorNome = "nome";

    /// <summary>RF06 "Parâmetros das fichas": title printed at the top of the ficha; empty = the ficha prints no title line.</summary>
    public static readonly DefinicaoParametro<string> TituloFichas =
        new("tituloFichas", Padrao: string.Empty, valor => InterpretarTexto(valor, TamanhoMaximoTitulo));

    /// <summary>RF06 "Parâmetros das fichas": footer message printed at the bottom of the ficha; empty = no footer.</summary>
    public static readonly DefinicaoParametro<string> MensagemRodapeFicha =
        new("mensagemRodapeFicha", Padrao: string.Empty, valor => InterpretarTexto(valor, TamanhoMaximoRodape));

    /// <summary>
    /// RF06 "impressão do QR da ficha". Off by default: the QR points to the public ficha page, which does not exist
    /// yet (requisitos RF11 l.368 and 7.1), so the totem reads it but prints no QR until that page ships.
    /// </summary>
    public static readonly DefinicaoParametro<bool> ImprimirQrFicha =
        new("imprimirQrFicha", Padrao: false, InterpretarBooleano);

    /// <summary>RF06 "Teclas de atalho do guichê", legacy defaults F7, F8, F4, F6, F3 and F2 (requisitos l.233).</summary>
    public static readonly DefinicaoParametro<string> TeclaChamarProxima = new("teclaChamarProxima", Padrao: "F7", InterpretarTecla);

    public static readonly DefinicaoParametro<string> TeclaEncaminhar = new("teclaEncaminhar", Padrao: "F8", InterpretarTecla);

    public static readonly DefinicaoParametro<string> TeclaEncerrar = new("teclaEncerrar", Padrao: "F4", InterpretarTecla);

    public static readonly DefinicaoParametro<string> TeclaEncerrarEChamar = new("teclaEncerrarEChamar", Padrao: "F6", InterpretarTecla);

    public static readonly DefinicaoParametro<string> TeclaIniciar = new("teclaIniciar", Padrao: "F3", InterpretarTecla);

    public static readonly DefinicaoParametro<string> TeclaChamarNovamente = new("teclaChamarNovamente", Padrao: "F2", InterpretarTecla);

    /// <summary>
    /// RF06 "Ordenação de filas": "ordem" keeps the order configured on the station (the legacy "prioridade normal"),
    /// "nome" sorts the filas by display name. Read by the totem buttons; F12's guichê grid reads it too.
    /// </summary>
    public static readonly DefinicaoParametro<string> OrdenacaoFilas =
        new("ordenacaoFilas", Padrao: OrdenacaoPorOrdem, valor => InterpretarOpcao(valor, [OrdenacaoPorOrdem, OrdenacaoPorNome]));

    /// <summary>RF06 "Painel: quantidade de fichas chamadas a exibir" (was the fixed 10 of ObterEstadoPainelQueryHandler).</summary>
    public static readonly DefinicaoParametro<int> QuantidadeChamadasPainel =
        new("quantidadeChamadasPainel", Padrao: 10, valor => InterpretarInteiroAte(valor, QuantidadeMaximaChamadasPainel));

    // --- C20: the other RF06 Geral keys (requisitos l.228, D29 and D30 of 17/10/2026). ---

    public const int TamanhoMaximoMensagemAoChamar = 120;
    public const string FalaSoSenha = "senha";
    public const string FalaSenhaEMensagem = "senhaEMensagem";

    /// <summary>RF06 "mensagem exibida ao chamar a ficha": shown on the painel under the called senha; empty = no line.</summary>
    public static readonly DefinicaoParametro<string> MensagemAoChamar =
        new("mensagemAoChamar", Padrao: string.Empty, valor => InterpretarTexto(valor, TamanhoMaximoMensagemAoChamar));

    /// <summary>
    /// RF06 "chamar ficha por voz" as amended by D29: RNF19 wants every call in voice and text, so the voice never turns
    /// off and the key only chooses what it says. An option, not a boolean, so "false" is refused (400) instead of
    /// silencing the painel. The senha alone is the default because requisitos l.86 never speaks the guichê.
    /// </summary>
    public static readonly DefinicaoParametro<string> ChamarFichaPorVoz =
        new("chamarFichaPorVoz", Padrao: FalaSoSenha, valor => InterpretarOpcao(valor, [FalaSoSenha, FalaSenhaEMensagem]));

    /// <summary>
    /// RF06 "exibir informação detalhada de fila" (D30): on, the guichê screen shows the waiting count of each fila of
    /// its station; off, only the total. Off by default, the smaller screen.
    /// </summary>
    public static readonly DefinicaoParametro<bool> ExibirInformacaoDetalhadaFila =
        new("exibirInformacaoDetalhadaFila", Padrao: false, InterpretarBooleano);

    /// <summary>The six guichê hotkeys; no two may hold the same key in the effective set of any unidade.</summary>
    public static IReadOnlyList<DefinicaoParametro<string>> Teclas { get; } =
        [TeclaChamarProxima, TeclaEncaminhar, TeclaEncerrar, TeclaEncerrarEChamar, TeclaIniciar, TeclaChamarNovamente];

    /// <summary>Every writable key, in the screen's order. A key outside this list is unknown to the API (404).</summary>
    public static IReadOnlyList<ParametroCatalogado> Todos { get; } =
    [
        ParametroCatalogado.Booleano(SomenteFichasDaDataAtual, "Geral"),
        ParametroCatalogado.Booleano(ExibirInformacaoDetalhadaFila, "Geral"),
        ParametroCatalogado.Opcao(ChamarFichaPorVoz, "Geral", [FalaSoSenha, FalaSenhaEMensagem]),
        ParametroCatalogado.Texto(MensagemAoChamar, "Geral"),
        ParametroCatalogado.Texto(TituloFichas, "Fichas"),
        ParametroCatalogado.Texto(MensagemRodapeFicha, "Fichas"),
        ParametroCatalogado.Booleano(ImprimirQrFicha, "Fichas"),
        .. Teclas.Select(t => ParametroCatalogado.Tecla(t, "Teclas de atalho do guichê")),
        ParametroCatalogado.Opcao(OrdenacaoFilas, "Ordenação de filas", [OrdenacaoPorOrdem, OrdenacaoPorNome]),
        ParametroCatalogado.Inteiro(QuantidadeChamadasPainel, "Painel"),
        ParametroCatalogado.Inteiro(FonteMinimaTotemPx, "Totem"),
    ];

    public static ParametroCatalogado? Buscar(string chave) => Todos.FirstOrDefault(p => p.Chave == chave);

    /// <summary>Printable text up to <paramref name="maximo"/> characters, trimmed; control characters would break the print layout.</summary>
    public static (bool Valido, string Valor) InterpretarTexto(string valor, int maximo)
    {
        ArgumentNullException.ThrowIfNull(valor);
        var texto = valor.Trim();
        return texto.Length <= maximo && !texto.Any(char.IsControl) ? (true, texto) : (false, string.Empty);
    }

    /// <summary>F1 to F12 only: letters and digits are typed into the guichê's own fields, so they cannot be shortcuts.</summary>
    public static (bool Valido, string Valor) InterpretarTecla(string valor)
    {
        ArgumentNullException.ThrowIfNull(valor);
        var tecla = valor.Trim().ToUpperInvariant();
        return tecla.Length is 2 or 3 && tecla[0] == 'F'
            && int.TryParse(tecla.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var numero)
            && numero is >= 1 and <= 12 && tecla[1] != '0'
                ? (true, tecla)
                : (false, string.Empty);
    }

    public static (bool Valido, string Valor) InterpretarOpcao(string valor, IReadOnlyList<string> opcoes)
    {
        ArgumentNullException.ThrowIfNull(valor);
        var texto = valor.Trim();
        return opcoes.Contains(texto, StringComparer.Ordinal) ? (true, texto) : (false, string.Empty);
    }

    public static (bool Valido, int Valor) InterpretarInteiroAte(string valor, int maximo)
    {
        var (valido, numero) = InterpretarInteiroPositivo(valor);
        return valido && numero <= maximo ? (true, numero) : (false, 0);
    }
}
