using FSH.Framework.Core.Domain;

namespace FSH.Modules.PAF.Domain;

/// <summary>
/// Vínculo entre o guichê selecionado e o seu dono: uma sessão do kit (UserSession) ou uma concessão
/// de impersonação (ImpersonationGrant), nunca as duas. Sem filtro de unidade e sem UnidadeId: a sessão
/// troca de unidade corrente ao longo do dia, e a unidade do guichê já está na estação (seção 4.1).
/// </summary>
public sealed class SessaoEstacao : BaseEntity<Guid>
{
    public Guid EstacaoId { get; private set; }
    public Guid? SessaoId { get; private set; }
    public Guid? ConcessaoId { get; private set; }

    /// <summary>Copiado do exp do token de impersonação na seleção; nulo quando a dona é sessão.</summary>
    public DateTimeOffset? ConcessaoExpiraEm { get; private set; }

#pragma warning disable S1144 // EF Core writes these setters via reflection
    public DateTimeOffset SelecionadoEm { get; private set; }
    public DateTimeOffset? LiberadoEm { get; private set; }
#pragma warning restore S1144

    private SessaoEstacao() { }

    public static SessaoEstacao CriarParaSessao(Guid estacaoId, Guid sessaoId) =>
        new() { Id = Guid.CreateVersion7(), EstacaoId = estacaoId, SessaoId = sessaoId };

    public static SessaoEstacao CriarParaConcessao(Guid estacaoId, Guid concessaoId, DateTimeOffset concessaoExpiraEm) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            EstacaoId = estacaoId,
            ConcessaoId = concessaoId,
            ConcessaoExpiraEm = concessaoExpiraEm,
        };
}
