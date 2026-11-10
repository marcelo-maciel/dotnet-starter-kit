using FSH.Framework.Jobs.Services;
using FSH.Modules.Identity.Contracts.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.Provisionamento;

/// <summary>
/// Hangfire job of D18: attaches the tenant's initial admin to the initial unidade once the kit has seeded him, and
/// seeds the three PAF roles (Identity is ready by then). Reschedules itself while the admin does not exist, up to
/// <see cref="Teto"/> attempts, then gives up with a warning. Every step is idempotent, so a duplicate run is harmless.
/// </summary>
public sealed partial class AssociacaoAdministradorInicial(
    IServiceScopeFactory scopeFactory,
    IJobService jobs,
    ILogger<AssociacaoAdministradorInicial> logger)
{
    public const int Teto = 20;
    public static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(30);

    public async Task ExecutarAsync(string tenantId, int tentativa, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (await ProvisionamentoTenantPaf.LerTenantAsync(scopeFactory, tenantId) is not { } tenant)
        {
            LogTenantAusente(logger, tenantId);
            return;
        }

        await using var escopo = ProvisionamentoTenantPaf.AbrirEscopo(scopeFactory, tenant);
        var servicos = escopo.ServiceProvider;
        var adminEmail = tenant.AdminEmail;
        // ponytail: the contract has no lookup by e-mail, so the tenant's user list is scanned; at creation it holds
        // one user. Swap for a contract lookup if this job ever runs on large tenants.
        var admin = (await servicos.GetRequiredService<IUserService>().GetListAsync(ct))
            .FirstOrDefault(u => string.Equals(u.Email, adminEmail, StringComparison.OrdinalIgnoreCase));
        if (admin?.Id is not { } adminId)
        {
            Reagendar(tenantId, tentativa, ct);
            return;
        }

        await PapeisPadrao.SemearAsync(servicos.GetRequiredService<IRoleService>(), ct);

        var provisionamento = ActivatorUtilities.CreateInstance<ProvisionamentoTenantPaf>(servicos);
        var unidadeId = await provisionamento.GarantirUnidadeInicialAsync(
            tenant.Name ?? tenantId, ct);
        var gravou = await provisionamento.AssociarAdministradorAsync(tenantId, Guid.Parse(adminId), unidadeId, ct);
        LogAssociado(logger, tenantId, unidadeId, tentativa, gravou);
    }

    private void Reagendar(string tenantId, int tentativa, CancellationToken ct)
    {
        if (tentativa >= Teto)
        {
            LogDesistiu(logger, tenantId, tentativa);
            return;
        }

        LogAdminAusente(logger, tenantId, tentativa);
        jobs.Schedule<AssociacaoAdministradorInicial>(j => j.ExecutarAsync(tenantId, tentativa + 1, ct), Intervalo);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "[PAF] tenant {TenantId} não existe mais; associação do administrador inicial abandonada")]
    private static partial void LogTenantAusente(ILogger logger, string tenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[PAF] tenant {TenantId}: administrador inicial ainda não existe (tentativa {Tentativa}); reagendado")]
    private static partial void LogAdminAusente(ILogger logger, string tenantId, int tentativa);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[PAF] tenant {TenantId}: administrador inicial não apareceu em {Tentativa} tentativas; associação abandonada, conceda o escopo pelo cadastro")]
    private static partial void LogDesistiu(ILogger logger, string tenantId, int tentativa);

    [LoggerMessage(Level = LogLevel.Information, Message = "[PAF] tenant {TenantId}: administrador inicial na unidade {UnidadeId} (tentativa {Tentativa}, gravou={Gravou})")]
    private static partial void LogAssociado(ILogger logger, string tenantId, Guid unidadeId, int tentativa, bool gravou);
}
