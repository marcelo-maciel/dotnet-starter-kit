namespace FSH.Modules.PAF.Escopo;

/// <summary>Holder com escopo de requisição; nasce vazio, e vazio é fechado (filtro devolve nada).</summary>
public sealed class UnidadeCorrente : IUnidadeCorrente
{
    public Guid? UnidadeId { get; private set; }

    public void Definir(Guid unidadeId)
    {
        if (unidadeId == Guid.Empty)
        {
            throw new ArgumentException("Unidade corrente não pode ser vazia.", nameof(unidadeId));
        }

        UnidadeId = unidadeId;
    }
}
