using FSH.Framework.Core.Domain;

namespace FSH.Modules.PAF.Domain;

/// <summary>
/// Parâmetro de empresa (UnidadeId nulo) ou de unidade. Fica fora de <see cref="IEscopoUnidade"/>
/// porque o nível empresa não tem unidade; o PafDbContext aplica a ele um filtro próprio.
/// </summary>
public sealed class Parametro : BaseEntity<Guid>
{
    public Guid? UnidadeId { get; private set; }
    public NivelParametro Nivel { get; private set; }
    public string Chave { get; private set; } = default!;
    public string Valor { get; private set; } = default!;

    private Parametro() { }

    public static Parametro CriarDeEmpresa(string chave, string valor) => Criar(null, NivelParametro.Empresa, chave, valor);

    public static Parametro CriarDeUnidade(Guid unidadeId, string chave, string valor) => Criar(unidadeId, NivelParametro.Unidade, chave, valor);

    private static Parametro Criar(Guid? unidadeId, NivelParametro nivel, string chave, string valor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chave);
        ArgumentNullException.ThrowIfNull(valor);
        return new Parametro { Id = Guid.CreateVersion7(), UnidadeId = unidadeId, Nivel = nivel, Chave = chave, Valor = valor };
    }
}
