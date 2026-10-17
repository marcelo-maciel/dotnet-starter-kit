using FSH.Framework.Core.Domain;

namespace FSH.Modules.PAF.Domain;

/// <summary>
/// Cadastro da estação (RF04). A credencial do dispositivo vive em <see cref="VinculoEstacao"/>.
/// Os instantes de conexão existem só para os papéis que usam credencial de estação, emissão e painel.
/// </summary>
public sealed class Estacao : BaseEntity<Guid>, IEscopoUnidade
{
    public Guid UnidadeId { get; private set; }
    public string Nome { get; private set; } = default!;
    public TipoEstacao Tipo { get; private set; }

#pragma warning disable S1144 // EF Core writes these setters via reflection
    /// <summary>Bloco de configuração do tipo, em JSON.</summary>
    public string? Configuracao { get; private set; }

    public bool Ativa { get; private set; }
    public DateTimeOffset? UltimaConexaoEmissaoEm { get; private set; }
    public DateTimeOffset? UltimaQuedaEmissaoEm { get; private set; }
    public DateTimeOffset? UltimaConexaoPainelEm { get; private set; }
    public DateTimeOffset? UltimaQuedaPainelEm { get; private set; }
#pragma warning restore S1144

    private Estacao() { }

    public static Estacao Criar(Guid unidadeId, string nome, TipoEstacao tipo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nome);
        return new Estacao
        {
            Id = Guid.CreateVersion7(),
            UnidadeId = unidadeId,
            Nome = nome,
            Tipo = tipo,
            Ativa = true,
        };
    }
}
