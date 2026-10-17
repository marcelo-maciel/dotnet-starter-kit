using FSH.Framework.Core.Domain;

namespace FSH.Modules.PAF.Domain;

/// <summary>
/// Associação entre fila e prioridade, com a letra da senha. UnidadeId é denormalizado porque a
/// letra é única na unidade inteira, e o índice único precisa da coluna (seção 4.2).
/// </summary>
public sealed class FilaPrioridade : BaseEntity<Guid>, IEscopoUnidade
{
    public const int LetraMaxLength = 5;

    public Guid UnidadeId { get; private set; }
    public Guid FilaId { get; private set; }
    public Guid PrioridadeId { get; private set; }
    public string Letra { get; private set; } = default!;
    public int NumeroInicial { get; private set; }
#pragma warning disable S1144 // EF Core writes these setters via reflection
    public string? CorBotao { get; private set; }
#pragma warning restore S1144
    public int Ordem { get; private set; }

    private FilaPrioridade() { }

    public static FilaPrioridade Criar(Guid unidadeId, Guid filaId, Guid prioridadeId, string letra, int numeroInicial, int ordem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(letra);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(letra.Length, LetraMaxLength);
        return new FilaPrioridade
        {
            Id = Guid.CreateVersion7(),
            UnidadeId = unidadeId,
            FilaId = filaId,
            PrioridadeId = prioridadeId,
            Letra = letra,
            NumeroInicial = numeroInicial,
            Ordem = ordem,
        };
    }
}
