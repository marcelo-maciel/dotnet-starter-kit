namespace FSH.Modules.PAF.Parametros;

/// <summary>Two guichê actions bound to the same key: the guichê could not tell which one the operator meant.</summary>
public sealed record TeclaRepetida(string Tecla, string PrimeiraChave, string SegundaChave);

/// <summary>
/// Hotkey conflicts of RF06 in one effective set: unidade rows over empresa rows over the catalog default, the same
/// resolution the guichê reads. An unreadable stored key counts as its default, as it does for the reader.
/// </summary>
public static class ConflitoDeTeclas
{
    public static TeclaRepetida? Encontrar(IReadOnlyDictionary<string, string> empresa, IReadOnlyDictionary<string, string> unidade)
    {
        ArgumentNullException.ThrowIfNull(empresa);
        ArgumentNullException.ThrowIfNull(unidade);
        var donos = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definicao in CatalogoParametros.Teclas)
        {
            var tecla = Efetiva(definicao, empresa, unidade);
            if (donos.TryGetValue(tecla, out var outra))
            {
                return new TeclaRepetida(tecla, outra, definicao.Chave);
            }

            donos[tecla] = definicao.Chave;
        }

        return null;
    }

    private static string Efetiva(
        DefinicaoParametro<string> definicao, IReadOnlyDictionary<string, string> empresa, IReadOnlyDictionary<string, string> unidade)
    {
        var texto = unidade.GetValueOrDefault(definicao.Chave) ?? empresa.GetValueOrDefault(definicao.Chave);
        if (texto is null)
        {
            return definicao.Padrao;
        }

        var (valido, valor) = definicao.Interpretar(texto);
        return valido ? valor : definicao.Padrao;
    }
}
