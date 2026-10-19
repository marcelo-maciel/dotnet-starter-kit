using FSH.Framework.Eventing.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Events;
using FSH.Modules.PAF.TempoReal;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// A session revoked by any kit path (sessions screen, admin, revoke-all; S01): the PAF does what the renewal cycle
/// does for a dead session (seção 7 and 9), without waiting for it: releases licence and guichê, drops the Valkey
/// state and closes the session's hub connections with <see cref="FSH.Modules.PAF.Contracts.v1.TempoReal.SessaoInvalidada"/>.
/// Every step is idempotent: a PAF-born revocation also raises this event, and the session id marked by the first
/// notice drops the second; the same event delivered twice is dropped by the kit inbox. A lost event is covered by
/// the renewal cycle.
/// </summary>
public sealed partial class SessaoRevogadaPafHandler(
    Licencas licencas,
    EstadoSessao estado,
    ConexoesPaf conexoes,
    ILogger<SessaoRevogadaPafHandler> logger)
    : IIntegrationEventHandler<SessionRevokedIntegrationEvent>
{
    // The kit's reason is the revoker's audit text, not written for the operator; the screen gets a fixed sentence.
    public const string MensagemSessaoEncerrada = "Sua sessão foi encerrada. Entre novamente.";

    public async Task HandleAsync(SessionRevokedIntegrationEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (string.IsNullOrEmpty(@event.TenantId))
        {
            return;
        }

        // A failure propagates: the outbox delivers again, and every step below is idempotent.
        // The root tenant holds no licence nor unit state (as EndSessao); its operators' hub connections still close.
        if (!string.Equals(@event.TenantId, MultitenancyConstants.Root.Id, StringComparison.Ordinal))
        {
            await licencas.LiberarAsync(@event.TenantId, @event.SessionId, ct).ConfigureAwait(false);
            await estado.RemoverAsync(@event.TenantId, @event.SessionId, ct).ConfigureAwait(false);
        }

        var alcancadas = await conexoes.EncerrarSessaoAsync(@event.TenantId, @event.SessionId, MensagemSessaoEncerrada, ct)
            .ConfigureAwait(false);
        LogSessaoRevogada(logger, @event.TenantId, @event.SessionId, alcancadas);
    }

    [LoggerMessage(EventId = 9104, Level = LogLevel.Information,
        Message = "PAF: sessao revogada no kit tenant={TenantId} sessao={SessaoId}; conexoes encerradas={Conexoes}")]
    private static partial void LogSessaoRevogada(ILogger logger, string tenantId, Guid sessaoId, int conexoes);
}
