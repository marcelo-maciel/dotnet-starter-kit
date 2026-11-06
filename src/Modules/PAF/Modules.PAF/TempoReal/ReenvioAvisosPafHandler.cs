using FSH.Framework.Eventing.Abstractions;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.TempoReal;

/// <summary>
/// Outbox copy of every PAF notice (seção 8 item 6). Re-sends an atendimento notice only while the
/// atendimento is still in the state it announces, so a late copy of SenhaChamada never announces a
/// ficha cancelled in the meantime. The event bus installs the tenant before resolving this handler.
/// </summary>
public sealed partial class ReenvioAvisosPafHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    AvisosTempoReal avisos,
    ILogger<ReenvioAvisosPafHandler> logger)
    : IIntegrationEventHandler<SenhaChamadaPaf>,
      IIntegrationEventHandler<SenhaRechamadaPaf>,
      IIntegrationEventHandler<AtendimentoAtualizadoPaf>,
      IIntegrationEventHandler<FilaAtualizadaPaf>
{
    public Task HandleAsync(SenhaChamadaPaf @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        return ReenviarSeNoEstadoAsync(@event, @event.Aviso.UnidadeId, @event.Aviso.AtendimentoId, EstadoAtendimento.Chamado, ct);
    }

    public Task HandleAsync(SenhaRechamadaPaf @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        return ReenviarSeNoEstadoAsync(@event, @event.Aviso.UnidadeId, @event.Aviso.AtendimentoId, EstadoAtendimento.Chamado, ct);
    }

    public Task HandleAsync(AtendimentoAtualizadoPaf @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (!Enum.TryParse<EstadoAtendimento>(@event.Aviso.Estado, ignoreCase: false, out var estado))
        {
            // Retrying cannot fix a payload written with an unknown state; drop it loudly.
            LogEstadoDesconhecido(logger, @event.Id, @event.Aviso.Estado);
            return Task.CompletedTask;
        }

        return ReenviarSeNoEstadoAsync(@event, @event.Aviso.UnidadeId, @event.Aviso.AtendimentoId, estado, ct);
    }

    // A queue refresh carries no state to go stale: the screen re-queries the queue on arrival.
    public Task HandleAsync(FilaAtualizadaPaf @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        return avisos.EntregarAsync(@event, ct);
    }

    private async Task ReenviarSeNoEstadoAsync(AvisoPaf aviso, Guid unidadeId, Guid atendimentoId, EstadoAtendimento anunciado, CancellationToken ct)
    {
        // The dispatcher scope has no current unidade, and an empty one makes the unit filter return
        // nothing (fail-closed): without this the handler would silently never re-send.
        unidadeCorrente.Definir(unidadeId);

        var atual = await db.Atendimentos
            .AsNoTracking()
            .Where(a => a.Id == atendimentoId)
            .Select(a => (EstadoAtendimento?)a.Estado)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (atual != anunciado)
        {
            LogReenvioDescartado(logger, aviso.Id, atendimentoId, anunciado, atual);
            return;
        }

        await avisos.EntregarAsync(aviso, ct).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 9020, Level = LogLevel.Information,
        Message = "Reenvio descartado: atendimento {AtendimentoId} nao esta mais em {Anunciado} (atual {Atual}, vazio = inexistente); evento={EventoId}")]
    private static partial void LogReenvioDescartado(ILogger logger, Guid eventoId, Guid atendimentoId, EstadoAtendimento anunciado, EstadoAtendimento? atual);

    [LoggerMessage(EventId = 9021, Level = LogLevel.Error,
        Message = "Aviso AtendimentoAtualizado com estado desconhecido {Estado}; evento={EventoId}")]
    private static partial void LogEstadoDesconhecido(ILogger logger, Guid eventoId, string estado);
}
