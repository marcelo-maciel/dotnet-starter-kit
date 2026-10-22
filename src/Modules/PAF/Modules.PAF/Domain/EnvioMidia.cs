using FSH.Framework.Core.Domain;
using FSH.Modules.PAF.Midias;

namespace FSH.Modules.PAF.Domain;

/// <summary>
/// Um envio de arquivo ao armazenamento (RF03). É a fonte da chave do objeto: a chave nunca é
/// reaproveitada, então trocar o arquivo de uma mídia é um envio novo, e o envio que nenhuma mídia
/// referencia é o que a limpeza apaga depois do bloqueio de 30 dias. Os instantes vêm do PostgreSQL.
/// </summary>
public sealed class EnvioMidia : BaseEntity<Guid>, IEscopoUnidade
{
    public const int ChaveObjetoMaxLength = 512;
    public const int ContentTypeMaxLength = 128;

    public Guid UnidadeId { get; private set; }
    public string ChaveObjeto { get; private set; } = default!;
    public TipoMidia Tipo { get; private set; }

    /// <summary>MIME declarado no pedido do envio; a confirmação compara com o que o armazenamento guardou.</summary>
    public string ContentType { get; private set; } = default!;

#pragma warning disable S1144 // EF Core writes these setters via reflection
    public DateTimeOffset CriadoEm { get; private set; }
    public DateTimeOffset? ConfirmadoEm { get; private set; }
    public DateTimeOffset? RecusadoEm { get; private set; }

    /// <summary>Gravado só depois de conferir que o objeto sumiu do armazenamento.</summary>
    public DateTimeOffset? RemovidoEm { get; private set; }
#pragma warning restore S1144

    private EnvioMidia() { }

    /// <summary>O id vem de quem compõe a chave, porque ela o contém.</summary>
    public static EnvioMidia Criar(Guid id, Guid unidadeId, string chaveObjeto, TipoMidia tipo, string contentType)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("O id do envio é obrigatório.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(chaveObjeto);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chaveObjeto.Length, ChaveObjetoMaxLength);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(contentType.Length, ContentTypeMaxLength);
        return new EnvioMidia
        {
            Id = id,
            UnidadeId = unidadeId,
            ChaveObjeto = chaveObjeto,
            Tipo = tipo,
            ContentType = contentType,
        };
    }
}
