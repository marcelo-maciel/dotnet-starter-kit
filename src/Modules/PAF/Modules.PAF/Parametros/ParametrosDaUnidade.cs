namespace FSH.Modules.PAF.Parametros;

/// <summary>Where an effective value came from; the screen shows it so a store that behaves differently explains itself (RF06).</summary>
public enum OrigemParametro
{
    Fabrica,
    Empresa,
    Unidade,
}

/// <summary>
/// Every stored row of one unidade and of the empresa, read in one query (<see cref="LeitorParametros.CarregarAsync"/>),
/// resolved with the same rule as <see cref="LeitorParametros.LerAsync{T}"/>: unidade, else empresa, else the catalog
/// default; an unreadable stored text falls back to the default and is reported through <c>avisarInvalido</c>.
/// </summary>
public sealed class ParametrosDaUnidade(
    IReadOnlyDictionary<string, string> empresa,
    IReadOnlyDictionary<string, string> unidade,
    Action<string, OrigemParametro> avisarInvalido)
{
    public string? ValorDaEmpresa(string chave) => empresa.GetValueOrDefault(chave);

    public string? ValorDaUnidade(string chave) => unidade.GetValueOrDefault(chave);

    public T Ler<T>(DefinicaoParametro<T> definicao)
    {
        ArgumentNullException.ThrowIfNull(definicao);
        if (Armazenado(definicao.Chave) is not { } armazenado)
        {
            return definicao.Padrao;
        }

        var (texto, origem) = armazenado;
        var (valido, valor) = definicao.Interpretar(texto);
        if (valido)
        {
            return valor;
        }

        avisarInvalido(definicao.Chave, origem);
        return definicao.Padrao;
    }

    /// <summary>The effective value as canonical text, with its origin; an unreadable stored text counts as the default.</summary>
    public (string Valor, OrigemParametro Origem) Efetivo(ParametroCatalogado parametro)
    {
        ArgumentNullException.ThrowIfNull(parametro);
        if (Armazenado(parametro.Chave) is not { } guardado)
        {
            return (parametro.Padrao, OrigemParametro.Fabrica);
        }

        var (texto, origem) = guardado;
        if (parametro.Normalizar(texto) is { } valor)
        {
            return (valor, origem);
        }

        avisarInvalido(parametro.Chave, origem);
        return (parametro.Padrao, OrigemParametro.Fabrica);
    }

    private (string Texto, OrigemParametro Origem)? Armazenado(string chave)
    {
        if (unidade.TryGetValue(chave, out var daUnidade))
        {
            return (daUnidade, OrigemParametro.Unidade);
        }

        return empresa.TryGetValue(chave, out var daEmpresa) ? (daEmpresa, OrigemParametro.Empresa) : null;
    }
}
