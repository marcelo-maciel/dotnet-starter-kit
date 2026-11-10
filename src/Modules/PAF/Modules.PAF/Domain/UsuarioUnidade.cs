using FSH.Framework.Core.Domain;

namespace FSH.Modules.PAF.Domain;

/// <summary>
/// Unidade do escopo de um usuário, com ordem; a de menor ordem é a principal, onde começa a
/// cascata de licença. Escopo de tenant: descreve o usuário, não a operação da unidade corrente.
/// </summary>
public sealed class UsuarioUnidade : BaseEntity<Guid>
{
    public Guid UsuarioId { get; private set; }
    public Guid UnidadeId { get; private set; }
    public int Ordem { get; private set; }

    private UsuarioUnidade() { }

    public static UsuarioUnidade Criar(Guid usuarioId, Guid unidadeId, int ordem) =>
        new() { Id = Guid.CreateVersion7(), UsuarioId = usuarioId, UnidadeId = unidadeId, Ordem = ordem };

    public void DefinirOrdem(int ordem) => Ordem = ordem;
}
