using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Storage;
using FSH.Framework.Storage.Services;
using FSH.Modules.PAF.Domain;
using Microsoft.Extensions.Options;

namespace FSH.Modules.PAF.Midias;

/// <summary>What the store holds for an envio, judged against what the envio declared (RF03).</summary>
public enum DesfechoConferencia
{
    /// <summary>No object at the key yet: the browser has not finished the PUT. Nothing is recorded; the client may retry.</summary>
    NaoRecebido,
    Aceito,

    /// <summary>The stored type differs from the envio's.</summary>
    RecusadoTipo,
    RecusadoVazio,
    RecusadoTeto,
}

/// <summary>
/// The cadastro's only door to the kit's <see cref="IStorageService"/> (ADR 0007): every call runs under
/// <see cref="OpcoesMidia.PrazoArmazenamento"/> (RNF05), and a store that does not answer becomes a 503 inside it.
/// </summary>
public sealed class ArmazenamentoMidia(IStorageService armazenamento, IOptions<OpcoesMidia> opcoes)
{
    public const string MensagemSemResposta = "O armazenamento de mídia não respondeu a tempo. Tente de novo em instantes.";

    private readonly OpcoesMidia _opcoes = opcoes.Value;

    public long TetoBytes(TipoMidia tipo) => _opcoes.TetoBytes(tipo);

    /// <summary>
    /// The presigned PUT for the envio's key, signed with the envio's MIME. The kit's signature does not bind the size
    /// (<c>maxBytes</c> is ignored by the S3 provider), so the ceiling is enforced at confirmation.
    /// </summary>
    public Task<PresignedUploadUrl> GerarUrlEnvioAsync(EnvioMidia envio, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envio);
        return ComPrazoAsync(
            limite => armazenamento.GenerateUploadUrlAsync(envio.ChaveObjeto, envio.ContentType, TetoBytes(envio.Tipo), _opcoes.ValidadeUrlEnvio, limite),
            ct);
    }

    /// <summary>
    /// HEAD of the envio's object. The kit's S3 provider answers null both for a missing object and for any other store
    /// error, so a failing store reads as <see cref="DesfechoConferencia.NaoRecebido"/> (nothing recorded, retryable).
    /// Ponytail: no first-bytes sniffing; the MIME the store recorded is the one the signed PUT forced.
    /// </summary>
    public async Task<(DesfechoConferencia Desfecho, StoredObjectMetadata? Objeto)> ConferirAsync(EnvioMidia envio, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envio);
        var objeto = await ComPrazoAsync(limite => armazenamento.HeadObjectAsync(envio.ChaveObjeto, limite), ct).ConfigureAwait(false);
        return (Avaliar(envio, objeto, TetoBytes(envio.Tipo)), objeto);
    }

    public static DesfechoConferencia Avaliar(EnvioMidia envio, StoredObjectMetadata? objeto, long tetoBytes)
    {
        ArgumentNullException.ThrowIfNull(envio);
        if (objeto is null)
        {
            return DesfechoConferencia.NaoRecebido;
        }

        if (!string.Equals(TipoSemParametros(objeto.ContentType), envio.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            return DesfechoConferencia.RecusadoTipo;
        }

        if (objeto.SizeBytes <= 0)
        {
            return DesfechoConferencia.RecusadoVazio;
        }

        return objeto.SizeBytes > tetoBytes ? DesfechoConferencia.RecusadoTeto : DesfechoConferencia.Aceito;
    }

    /// <summary><c>video/mp4; codecs=avc1</c> → <c>video/mp4</c>: the RF03 table lists bare media types.</summary>
    public static string TipoSemParametros(string contentType)
    {
        ArgumentNullException.ThrowIfNull(contentType);
        var pontoEVirgula = contentType.IndexOf(';', StringComparison.Ordinal);
        return (pontoEVirgula < 0 ? contentType : contentType[..pontoEVirgula]).Trim();
    }

    /// <summary>
    /// The signed GET of the mídia's current object (M04). The local provider answers a server-relative path; resolving
    /// it is the caller's job (<see cref="UrlLeituraMidia"/>).
    /// </summary>
    public Task<Uri> GerarUrlLeituraAsync(Midia midia, TimeSpan validade, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(midia);
        return ComPrazoAsync(limite => armazenamento.GenerateDownloadUrlAsync(midia.ChaveObjeto, validade, null, limite), ct);
    }

    private async Task<T> ComPrazoAsync<T>(Func<CancellationToken, Task<T>> chamada, CancellationToken ct)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(_opcoes.PrazoArmazenamento);
        try
        {
            // WaitAsync bounds even a provider that ignores the token.
            return await chamada(limite.Token).WaitAsync(limite.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CustomException(MensagemSemResposta, [], HttpStatusCode.ServiceUnavailable);
        }
    }
}
