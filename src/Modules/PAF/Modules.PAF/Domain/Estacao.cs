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

    public const int RamalMaxLength = 20;

    /// <summary>Ramal telefônico da estação (RF04, campos gerais).</summary>
    public string? Ramal { get; private set; }

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

    public void Renomear(string nome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nome);
        Nome = nome;
    }

    /// <summary>Validated and serialized by the cadastro (Features/v1/Estacoes/ConfiguracaoDaEstacao).</summary>
    public void DefinirConfiguracao(string configuracao)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuracao);
        Configuracao = configuracao;
    }

    public void DefinirRamal(string? ramal) =>
        Ramal = string.IsNullOrWhiteSpace(ramal) ? null : ramal.Trim();

    public void Ativar() => Ativa = true;

    public void Desativar() => Ativa = false;
}
