namespace FSH.Modules.PAF.Domain;

/// <summary>
/// Unidade que ocupa a licença de uma sessão do kit e o último instante de atividade. Uma linha por
/// sessão. É a contagem de licenças com o Valkey fora e a fonte da reconstrução (seção 9).
/// Sem filtro de unidade.
/// </summary>
public sealed class AtividadeSessao
{
    public Guid SessaoId { get; private set; }
    public Guid UsuarioId { get; private set; }
    public Guid UnidadeId { get; private set; }
#pragma warning disable S1144 // EF Core writes these setters via reflection
    public DateTimeOffset UltimaAtividadeEm { get; private set; }
#pragma warning restore S1144

    private AtividadeSessao() { }

    public static AtividadeSessao Criar(Guid sessaoId, Guid usuarioId, Guid unidadeId) =>
        new() { SessaoId = sessaoId, UsuarioId = usuarioId, UnidadeId = unidadeId };
}
