using FSH.Framework.Core.Domain;

namespace FSH.Modules.PAF.Domain;

/// <summary>Prioridade de atendimento. Valor maior é chamado antes (seção 4 dos requisitos).</summary>
public sealed class Prioridade : BaseEntity<Guid>, IEscopoUnidade
{
    public Guid UnidadeId { get; private set; }
    public string Nome { get; private set; } = default!;
    public int Valor { get; private set; }
    public bool Ativa { get; private set; }

    private Prioridade() { }

    public static Prioridade Criar(Guid unidadeId, string nome, int valor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nome);
        return new Prioridade
        {
            Id = Guid.CreateVersion7(),
            UnidadeId = unidadeId,
            Nome = nome,
            Valor = valor,
            Ativa = true,
        };
    }

    public void Atualizar(string nome, int valor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nome);
        Nome = nome;
        Valor = valor;
    }

    public void Ativar() => Ativa = true;

    public void Desativar() => Ativa = false;
}
