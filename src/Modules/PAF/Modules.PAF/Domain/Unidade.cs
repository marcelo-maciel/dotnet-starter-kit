using FSH.Framework.Core.Domain;

namespace FSH.Modules.PAF.Domain;

/// <summary>Loja, filial ou ponto de atendimento do tenant (ADR 0002). Escopo de tenant, sem filtro de unidade.</summary>
public sealed class Unidade : BaseEntity<Guid>
{
    public string Nome { get; private set; } = default!;
    public string? DocumentoFiscal { get; private set; }
    public bool Ativa { get; private set; }

#pragma warning disable S1144 // EF Core writes these setters via reflection
    /// <summary>Carimbado pelo PostgreSQL; ordena a cascata de licença do usuário de escopo empresa.</summary>
    public DateTimeOffset CriadoEm { get; private set; }
#pragma warning restore S1144

    private Unidade() { }

    public static Unidade Criar(string nome, string? documentoFiscal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nome);
        return new Unidade
        {
            Id = Guid.CreateVersion7(),
            Nome = nome,
            DocumentoFiscal = documentoFiscal,
            Ativa = true,
        };
    }

    public void Atualizar(string nome, string? documentoFiscal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nome);
        Nome = nome;
        DocumentoFiscal = documentoFiscal;
    }

    public void Ativar() => Ativa = true;

    public void Desativar() => Ativa = false;
}
