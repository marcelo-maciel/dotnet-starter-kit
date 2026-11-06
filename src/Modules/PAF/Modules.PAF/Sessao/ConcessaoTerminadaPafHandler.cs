using FSH.Framework.Eventing.Abstractions;
using FSH.Modules.Identity.Contracts.Events;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.TempoReal;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// An impersonation grant ended or revoked in the kit (S02, ARQ seção 9 "o vínculo de impersonação segue a vida da
/// concessão"): the PAF releases the grant's guichê link, takes the grant's hub connections out of every group (aborting
/// the ones held here) and drops the grant's unit in the Valkey, as it does for a revoked session. The atendimento in
/// progress stays on the guichê (REQ l.599). Ended and revoked count the same. Every step is idempotent: the same
/// event twice is dropped by the kit inbox, and a redelivery finds the link already released. Expiry raises no event:
/// <c>CloseOnAuthenticationExpiration</c> cuts the connection, the next selection of that guichê and the S08 job
/// release the link.
/// </summary>
public sealed partial class ConcessaoTerminadaPafHandler(
    PafDbContext db,
    EstadoSessao estado,
    ConexoesPaf conexoes,
    ILogger<ConcessaoTerminadaPafHandler> logger)
    : IIntegrationEventHandler<ImpersonationGrantTerminatedIntegrationEvent>
{
    public async Task HandleAsync(ImpersonationGrantTerminatedIntegrationEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (string.IsNullOrEmpty(@event.TenantId) || string.IsNullOrEmpty(@event.Jti))
        {
            return;
        }

        // The link and the Valkey key carry the jti as a Guid (ConcessaoImpersonacao refuses any other token), the
        // connection registry the raw claim: a jti that is not a Guid never reached a guichê, but its connections close.
        var soltos = 0;
        var concessaoId = Guid.TryParse(@event.Jti, out var id) ? id : (Guid?)null;
        if (concessaoId is { } concessao)
        {
            // A failure propagates: the outbox delivers again, and every step is idempotent.
            soltos = await VinculosGuiche.SoltarDaConcessaoAsync(db, @event.TenantId, concessao, ct).ConfigureAwait(false);
        }

        var encerradas = await conexoes.EncerrarConcessaoAsync(@event.TenantId, @event.Jti, ct).ConfigureAwait(false);

        if (concessaoId is { } chave)
        {
            // Without Valkey the key expires with the token; nothing reads it for a dead grant anyway.
            await estado.RemoverUnidadeDaConcessaoAsync(@event.TenantId, chave, ct).ConfigureAwait(false);
        }

        LogConcessaoTerminada(logger, @event.TenantId, @event.GrantId, soltos, encerradas);
    }

    [LoggerMessage(EventId = 9106, Level = LogLevel.Information,
        Message = "PAF: concessao de impersonacao terminada no kit tenant={TenantId} concessao={GrantId}; vinculos soltos={Vinculos} conexoes encerradas={Conexoes}")]
    private static partial void LogConcessaoTerminada(ILogger logger, string tenantId, Guid grantId, int vinculos, int conexoes);
}
