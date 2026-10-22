using FSH.Framework.Core.Domain;

namespace FSH.Modules.PAF.Domain;

/// <summary>Agrupamento opcional de filas, usado só pelos relatórios do RF07.</summary>
public sealed class Setor : BaseEntity<Guid>, IEscopoUnidade
{
    public Guid UnidadeId { get; private set; }
    public string Nome { get; private set; } = default!;

    private Setor() { }

    public static Setor Criar(Guid unidadeId, string nome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nome);
        return new Setor { Id = Guid.CreateVersion7(), UnidadeId = unidadeId, Nome = nome };
    }

    /// <summary>No deletion (RF02): a fila keeps pointing at its setor, and the reports group the past by it.</summary>
    public void Renomear(string nome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nome);
        Nome = nome;
    }
}
