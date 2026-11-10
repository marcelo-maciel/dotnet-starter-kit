using FSH.Framework.Core.Domain;

namespace FSH.Modules.PAF.Domain;

/// <summary>
/// Passagem de uma ficha por uma fila. Fila e prioridade são as do atendimento, não as da emissão.
/// Toda mudança de estado acontece sob FOR UPDATE nas linhas da ficha, carimba o marco com o relógio
/// do PostgreSQL e incrementa <see cref="Versao"/> na mesma escrita (seção 8, ADR 0005).
/// </summary>
public sealed class Atendimento : BaseEntity<Guid>, IEscopoUnidade
{
    public Guid UnidadeId { get; private set; }
    public Guid FichaId { get; private set; }
    public Guid FilaId { get; private set; }
    public Guid PrioridadeId { get; private set; }
#pragma warning disable S1144 // EF Core writes these setters via reflection
    public Guid? EstacaoId { get; private set; }
    public Guid? UsuarioId { get; private set; }
    public EstadoAtendimento Estado { get; private set; }

    /// <summary>Dia em que a passagem começou, derivado de CriadoEm. Imutável; é o dia da estatística.</summary>
    public DateOnly DataOperacional { get; private set; }

    /// <summary>Dia de ElegivelDesde; acompanha cada reescrita do marco. É o filtro "somente a data atual".</summary>
    public DateOnly DataElegibilidade { get; private set; }

    public DateTimeOffset CriadoEm { get; private set; }
    public DateTimeOffset ElegivelDesde { get; private set; }
    public DateTimeOffset? ChamadoEm { get; private set; }
    public DateTimeOffset? UltimaRechamadaEm { get; private set; }
    public DateTimeOffset? IniciadoEm { get; private set; }
    public DateTimeOffset? EncerradoEm { get; private set; }
    public DateTimeOffset? CanceladoEm { get; private set; }
    public Guid? AtendimentoOrigemId { get; private set; }
    public int Versao { get; private set; }
#pragma warning restore S1144

    private Atendimento() { }

    public static Atendimento Criar(Guid unidadeId, Guid fichaId, Guid filaId, Guid prioridadeId, Guid? atendimentoOrigemId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UnidadeId = unidadeId,
            FichaId = fichaId,
            FilaId = filaId,
            PrioridadeId = prioridadeId,
            Estado = EstadoAtendimento.Aguardando,
            AtendimentoOrigemId = atendimentoOrigemId,
        };
}
