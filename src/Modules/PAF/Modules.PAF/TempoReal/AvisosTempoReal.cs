using FSH.Framework.Eventing.Abstractions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.TempoReal;

/// <summary>
/// The two delivery paths of seção 8 item 6, as two calls the feature makes around its own commit:
/// <see cref="GravarAsync"/> inside the transaction of the state change, then
/// <see cref="EnviarAsync"/> right after the commit. Nothing is ever sent before the commit.
/// </summary>
public sealed partial class AvisosTempoReal(
    IOutboxWriter outbox,
    IHubContext<PafHub> hub,
    ILogger<AvisosTempoReal> logger)
{
    private static readonly TimeSpan LimiteEnvio = TimeSpan.FromSeconds(5);

    // The outbox store saves immediately and joins the ambient transaction of the scope's connection,
    // so the row commits or rolls back with the state change.
    public Task GravarAsync(AvisoPaf aviso, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(aviso);
        return outbox.AddAsync(aviso, ct);
    }

    /// <summary>
    /// Fast path. Any failure, cancellation included, is logged and swallowed: the state change is already committed and the
    /// outbox copy is the delivery guarantee.
    /// </summary>
    public async Task EnviarAsync(AvisoPaf aviso, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(aviso);
        try
        {
            await EntregarAsync(aviso, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogEnvioDiretoFalhou(logger, ex, aviso.GetType().Name, aviso.Id, aviso.TenantId);
        }
    }

    /// <summary>Guarantee path (outbox handler): a failure propagates so the dispatcher retries.</summary>
    internal async Task EntregarAsync(AvisoPaf aviso, CancellationToken ct)
    {
        // The send crosses the Redis backplane; a stalled Valkey must not hold the request or the dispatcher.
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(LimiteEnvio);
        await aviso.EntregarAsync(hub.Clients, limite.Token).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 9010, Level = LogLevel.Warning,
        Message = "Envio direto ao PafHub falhou; a caixa de saida reentrega. aviso={Aviso} evento={EventoId} tenant={TenantId}")]
    private static partial void LogEnvioDiretoFalhou(ILogger logger, Exception ex, string aviso, Guid eventoId, string? tenantId);
}
