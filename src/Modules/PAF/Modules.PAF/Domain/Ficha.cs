using FSH.Framework.Core.Domain;

namespace FSH.Modules.PAF.Domain;

/// <summary>
/// Senha emitida. Fila e prioridade são as da emissão e nunca mudam: compõem a chave da numeração.
/// CriadoEm e DataOperacional vêm do PostgreSQL, nunca do processo (seção 4.2).
/// </summary>
public sealed class Ficha : BaseEntity<Guid>, IEscopoUnidade
{
    public const int ChaveIdempotenciaMaxLength = 128;

    public Guid UnidadeId { get; private set; }
    public Guid FilaId { get; private set; }
    public Guid PrioridadeId { get; private set; }
    public string Letra { get; private set; } = default!;
    public int Numero { get; private set; }
    public Guid IdentificadorPublico { get; private set; }
    public CanalEmissao Canal { get; private set; }
    public Guid EstacaoId { get; private set; }
    public string ChaveIdempotencia { get; private set; } = default!;
#pragma warning disable S1144 // EF Core writes these setters via reflection
    public DateTimeOffset CriadoEm { get; private set; }
    public DateOnly DataOperacional { get; private set; }
#pragma warning restore S1144

    private Ficha() { }

    public static Ficha Criar(
        Guid unidadeId,
        Guid filaId,
        Guid prioridadeId,
        string letra,
        int numero,
        CanalEmissao canal,
        Guid estacaoId,
        string chaveIdempotencia)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(letra);
        ArgumentException.ThrowIfNullOrWhiteSpace(chaveIdempotencia);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chaveIdempotencia.Length, ChaveIdempotenciaMaxLength);
        return new Ficha
        {
            Id = Guid.CreateVersion7(),
            UnidadeId = unidadeId,
            FilaId = filaId,
            PrioridadeId = prioridadeId,
            Letra = letra,
            Numero = numero,
            // Random v4, not v7: the public id must not leak emission time or order.
            IdentificadorPublico = Guid.NewGuid(),
            Canal = canal,
            EstacaoId = estacaoId,
            ChaveIdempotencia = chaveIdempotencia,
        };
    }
}
