using FSH.Framework.Core.Domain;

namespace FSH.Modules.PAF.Domain;

/// <summary>
/// Escrita do PAF feita por impersonação, gravada na mesma transação. Existe porque a auditoria por
/// requisição do kit grava só o impersonado (sub); o operador vem da claim act_sub (seção 9).
/// </summary>
public sealed class AtoImpersonacao : BaseEntity<Guid>, IEscopoUnidade
{
    public Guid UnidadeId { get; private set; }
    public Guid ConcessaoId { get; private set; }
    public Guid OperadorId { get; private set; }
    public Guid? AtendimentoId { get; private set; }
#pragma warning disable S1144 // EF Core writes these setters via reflection
    public DateTimeOffset RegistradoEm { get; private set; }
#pragma warning restore S1144

    private AtoImpersonacao() { }

    public static AtoImpersonacao Criar(Guid unidadeId, Guid concessaoId, Guid operadorId, Guid? atendimentoId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UnidadeId = unidadeId,
            ConcessaoId = concessaoId,
            OperadorId = operadorId,
            AtendimentoId = atendimentoId,
        };
}
