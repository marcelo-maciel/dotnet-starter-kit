namespace FSH.Modules.PAF.Domain;

/// <summary>Prioridade habilitada numa estação de guichê, com ordem.</summary>
public sealed class EstacaoPrioridade : IEscopoUnidade
{
    public Guid EstacaoId { get; private set; }
    public Guid PrioridadeId { get; private set; }
    public Guid UnidadeId { get; private set; }
    public int Ordem { get; private set; }

    private EstacaoPrioridade() { }

    public static EstacaoPrioridade Criar(Guid unidadeId, Guid estacaoId, Guid prioridadeId, int ordem) =>
        new() { UnidadeId = unidadeId, EstacaoId = estacaoId, PrioridadeId = prioridadeId, Ordem = ordem };
}
