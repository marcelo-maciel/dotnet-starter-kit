namespace FSH.Modules.PAF.Domain;

/// <summary>
/// Item da lista de exibição de um painel (RF04, RF21 emendado em 17/10/2026): toca em laço pela
/// <see cref="Ordem"/>. Chave própria e não (estação, mídia), porque a mesma mídia pode voltar na lista,
/// como uma vinheta entre dois vídeos. Estação e mídia são da mesma unidade da linha (FKs compostas).
/// </summary>
public sealed class PainelMidia : IEscopoUnidade
{
    public Guid Id { get; private set; }
    public Guid UnidadeId { get; private set; }
    public Guid EstacaoId { get; private set; }
    public Guid MidiaId { get; private set; }
    public int Ordem { get; private set; }

    private PainelMidia() { }

    public static PainelMidia Criar(Guid unidadeId, Guid estacaoId, Guid midiaId, int ordem) =>
        new() { Id = Guid.CreateVersion7(), UnidadeId = unidadeId, EstacaoId = estacaoId, MidiaId = midiaId, Ordem = ordem };
}
