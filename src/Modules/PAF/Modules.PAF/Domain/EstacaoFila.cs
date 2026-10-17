namespace FSH.Modules.PAF.Domain;

/// <summary>
/// Fila de uma estação num papel. O papel está na chave porque os tipos combinados têm listas
/// independentes por papel; a chamada e a cobertura do RF02 leem só o papel guichê.
/// </summary>
public sealed class EstacaoFila : IEscopoUnidade
{
    public Guid EstacaoId { get; private set; }
    public PapelEstacao Papel { get; private set; }
    public Guid FilaId { get; private set; }
    public Guid UnidadeId { get; private set; }
    public int Ordem { get; private set; }

    private EstacaoFila() { }

    public static EstacaoFila Criar(Guid unidadeId, Guid estacaoId, PapelEstacao papel, Guid filaId, int ordem) =>
        new() { UnidadeId = unidadeId, EstacaoId = estacaoId, Papel = papel, FilaId = filaId, Ordem = ordem };

    public void DefinirOrdem(int ordem) => Ordem = ordem;
}
