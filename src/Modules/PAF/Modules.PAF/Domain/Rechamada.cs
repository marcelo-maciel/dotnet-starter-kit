using FSH.Framework.Core.Domain;

namespace FSH.Modules.PAF.Domain;

/// <summary>Registro de cada rechamada; permanece mesmo depois da reabilitação do atendimento.</summary>
public sealed class Rechamada : BaseEntity<Guid>, IEscopoUnidade
{
    public Guid UnidadeId { get; private set; }
    public Guid AtendimentoId { get; private set; }
    public Guid EstacaoId { get; private set; }
    public Guid UsuarioId { get; private set; }
#pragma warning disable S1144 // EF Core writes these setters via reflection
    public DateTimeOffset RechamadoEm { get; private set; }
#pragma warning restore S1144

    private Rechamada() { }

    public static Rechamada Criar(Guid unidadeId, Guid atendimentoId, Guid estacaoId, Guid usuarioId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UnidadeId = unidadeId,
            AtendimentoId = atendimentoId,
            EstacaoId = estacaoId,
            UsuarioId = usuarioId,
        };
}
