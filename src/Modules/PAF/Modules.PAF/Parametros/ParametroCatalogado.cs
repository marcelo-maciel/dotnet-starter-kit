using System.Globalization;

namespace FSH.Modules.PAF.Parametros;

/// <summary>How the screen edits a parameter; the API sends the name.</summary>
public enum TipoParametro
{
    Booleano,
    Inteiro,
    Texto,
    Opcao,
    Tecla,
}

/// <summary>
/// A <see cref="DefinicaoParametro{T}"/> seen without its type, so the API can list, validate and store every key
/// with one code path. <see cref="Normalizar"/> returns the canonical text to store, or null when the text is invalid
/// (the write answers 400): the same parse the reader uses, so a stored value is always readable.
/// </summary>
public sealed record ParametroCatalogado(
    string Chave,
    string Grupo,
    TipoParametro Tipo,
    string Padrao,
    IReadOnlyList<string> Opcoes,
    Func<string, string?> Normalizar)
{
    internal static ParametroCatalogado Booleano(DefinicaoParametro<bool> definicao, string grupo) =>
        De(definicao, grupo, TipoParametro.Booleano, [], v => v ? "true" : "false");

    internal static ParametroCatalogado Inteiro(DefinicaoParametro<int> definicao, string grupo) =>
        De(definicao, grupo, TipoParametro.Inteiro, [], v => v.ToString(CultureInfo.InvariantCulture));

    internal static ParametroCatalogado Texto(DefinicaoParametro<string> definicao, string grupo) =>
        De(definicao, grupo, TipoParametro.Texto, [], v => v);

    internal static ParametroCatalogado Tecla(DefinicaoParametro<string> definicao, string grupo) =>
        De(definicao, grupo, TipoParametro.Tecla, [], v => v);

    internal static ParametroCatalogado Opcao(DefinicaoParametro<string> definicao, string grupo, IReadOnlyList<string> opcoes) =>
        De(definicao, grupo, TipoParametro.Opcao, opcoes, v => v);

    private static ParametroCatalogado De<T>(
        DefinicaoParametro<T> definicao, string grupo, TipoParametro tipo, IReadOnlyList<string> opcoes, Func<T, string> formatar) =>
        new(definicao.Chave, grupo, tipo, formatar(definicao.Padrao), opcoes, texto =>
        {
            var (valido, valor) = definicao.Interpretar(texto);
            return valido ? formatar(valor) : null;
        });
}
