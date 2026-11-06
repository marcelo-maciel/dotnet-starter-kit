using FSH.Framework.Eventing.Abstractions;
using FSH.Framework.Jobs.Services;
using FSH.Modules.Multitenancy.Contracts.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.Provisionamento;

/// <summary>
/// A tenant created through the kit (RF23): the initial unidade is written now, and the initial admin is attached by
/// <see cref="AssociacaoAdministradorInicial"/>, because the event leaves when provisioning starts and the admin only
/// exists after the seed step (D18). Demo tenants of the DbMigrator publish no event: <c>PafDemoSeeder</c> covers them.
/// </summary>
public sealed partial class TenantCriadoPafHandler(
    IServiceScopeFactory scopeFactory,
    IJobService jobs,
    ILogger<TenantCriadoPafHandler> logger)
    : IIntegrationEventHandler<TenantSubscribedIntegrationEvent>
{
    public async Task HandleAsync(TenantSubscribedIntegrationEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var tenantId = @event.TenantId
            ?? throw new InvalidOperationException("TenantSubscribedIntegrationEvent sem TenantId.");

        // A failure here propagates: the outbox delivers the event again, and every step below is idempotent.
        var tenant = await ProvisionamentoTenantPaf.LerTenantAsync(scopeFactory, tenantId)
            ?? throw new InvalidOperationException($"Tenant '{tenantId}' do evento não existe no catálogo.");
        await using var escopo = ProvisionamentoTenantPaf.AbrirEscopo(scopeFactory, tenant);
        var nome = tenant.Name ?? tenantId;
        var provisionamento = ActivatorUtilities.CreateInstance<ProvisionamentoTenantPaf>(escopo.ServiceProvider);
        var unidadeId = await provisionamento.GarantirUnidadeInicialAsync(nome, ct);

        jobs.Enqueue<AssociacaoAdministradorInicial>(j => j.ExecutarAsync(tenantId, 1, ct));
        LogUnidadeInicial(logger, tenantId, unidadeId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[PAF] tenant {TenantId}: unidade inicial {UnidadeId}; associação do administrador agendada")]
    private static partial void LogUnidadeInicial(ILogger logger, string tenantId, Guid unidadeId);
}
