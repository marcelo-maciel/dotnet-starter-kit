using FSH.Framework.Core.Domain;

namespace FSH.Modules.PAF.Domain;

/// <summary>
/// Programação de uma mídia (RF10). Datas e hora inicial são do relógio de parede de São Paulo. Tempo da
/// mídia, tempo total e hora final não são gravados: saem da duração da mídia na leitura, então editar a
/// duração da mídia move a hora final de todas as programações dela sem reescrevê-las.
/// </summary>
public sealed class ProgramacaoMidia : BaseEntity<Guid>, IEscopoUnidade
{
    public const int NomeMaxLength = 120;

    public Guid UnidadeId { get; private set; }
    public Guid MidiaId { get; private set; }
    public string Nome { get; private set; } = default!;
    public DateOnly DataInicial { get; private set; }
    public DateOnly DataFinal { get; private set; }
    public TimeOnly HoraInicial { get; private set; }
    public int Repeticoes { get; private set; }
    public DiasSemana Dias { get; private set; }

#pragma warning disable S1144 // EF Core writes these setters via reflection
    public bool Ativa { get; private set; }
    public int Versao { get; private set; }
    public DateTimeOffset CriadoEm { get; private set; }
#pragma warning restore S1144

    private ProgramacaoMidia() { }

    public static ProgramacaoMidia Criar(
        Guid unidadeId,
        Guid midiaId,
        string nome,
        DateOnly dataInicial,
        DateOnly dataFinal,
        TimeOnly horaInicial,
        int repeticoes,
        DiasSemana dias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nome);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nome.Length, NomeMaxLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(dataFinal, dataInicial);
        ArgumentOutOfRangeException.ThrowIfLessThan(repeticoes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan((int)dias, (int)DiasSemana.Domingo);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((int)dias, (int)DiasSemana.Todos);
        return new ProgramacaoMidia
        {
            Id = Guid.CreateVersion7(),
            UnidadeId = unidadeId,
            MidiaId = midiaId,
            Nome = nome,
            DataInicial = dataInicial,
            DataFinal = dataFinal,
            HoraInicial = horaInicial,
            Repeticoes = repeticoes,
            Dias = dias,
            Ativa = true,
        };
    }
}
