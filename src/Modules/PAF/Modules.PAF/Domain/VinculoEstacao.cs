using FSH.Framework.Core.Domain;

namespace FSH.Modules.PAF.Domain;

/// <summary>
/// Credencial do dispositivo da estação, com ciclo de vida próprio: link de uso único, resgate,
/// revogação sem apagar a estação. Guarda só hashes; o token em claro nunca é persistido.
/// Uma credencial por papel que usa credencial de estação (emissão e painel).
/// </summary>
public sealed class VinculoEstacao : BaseEntity<Guid>, IEscopoUnidade
{
    public const int HashMaxLength = 128;

    public Guid UnidadeId { get; private set; }
    public Guid EstacaoId { get; private set; }
    public string LinkHash { get; private set; } = default!;
#pragma warning disable S1144 // EF Core writes these setters via reflection
    public DateTimeOffset CriadoEm { get; private set; }
    public DateTimeOffset? ResgatadoEm { get; private set; }
    public DateTimeOffset? RevogadoEm { get; private set; }
    public string? CredencialEmissaoHash { get; private set; }
    public string? CredencialPainelHash { get; private set; }
#pragma warning restore S1144

    private VinculoEstacao() { }

    public static VinculoEstacao Criar(Guid unidadeId, Guid estacaoId, string linkHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkHash);
        return new VinculoEstacao
        {
            Id = Guid.CreateVersion7(),
            UnidadeId = unidadeId,
            EstacaoId = estacaoId,
            LinkHash = linkHash,
        };
    }
}
