using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// Reconstrução dos conjuntos de licença a partir do PostgreSQL, a fonte da verdade (seção 9): sessões com linha
/// fresca em <c>AtividadeSessao</c> e <c>UserSession</c> ativo voltam à unidade da linha, sem cascata. Roda quando
/// a conexão ao Valkey volta e quando a marca de reconstrução some (Valkey reiniciado sem dados ou esvaziado).
/// Até lá, a ocupação recebe <see cref="DesfechoOcupacao.NaoReconstruido"/> e decide pelo modo degradado.
/// Toda instância roda; duas reconstruções simultâneas chegam ao mesmo conjunto.
/// </summary>
public sealed class ReconstrucaoLicencas(
    IServiceScopeFactory escopos,
    ConjuntoLicencas conjunto,
    TimeSpan intervalo,
    ILogger<ReconstrucaoLicencas> logger) : BackgroundService
{
    // ponytail: fixed polling; the return of the Valkey (ConnectionRestored, a missing mark, or any degraded call
    // seen by ConjuntoLicencas) is acted on at the next tick, up to this interval late.
    public static readonly TimeSpan IntervaloVerificacao = TimeSpan.FromSeconds(15);

    // Starts pending: an instance that starts may follow logins decided in the degraded mode.
    private int _reconexaoPendente = 1;

    /// <summary>Verdadeiro quando todos os tenants foram reconstruídos e a marca gravada.</summary>
    public async Task<bool> ReconstruirAsync(CancellationToken ct)
    {
        if (await conjunto.MarcoAsync(ct) is not { } marco)
        {
            return false;
        }

        IEnumerable<AppTenantInfo> tenants;
        await using (var escopo = escopos.CreateAsyncScope())
        {
            tenants = await escopo.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAllAsync();
        }

        var sessoes = 0;
        foreach (var tenant in tenants.Where(t => !string.Equals(t.Id, MultitenancyConstants.Root.Id, StringComparison.Ordinal)))
        {
            List<MembroVivo> vivos;
            try
            {
                vivos = await MembrosVivosAsync(tenant, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A tenant whose database is down (a failed provisioning stays in the store) cannot log anyone in
                // nor renew a mark, so its set has nothing to gain from waiting; holding the global mark for it
                // would keep every other tenant in the degraded mode for as long as it stays down. Its live
                // sessions put their members back on the next renewal once the database returns.
                logger.LogError(ex, "Reconstrução do conjunto de licenças pulou o tenant {TenantId}: banco indisponível", tenant.Id);
                continue;
            }

            if (!await conjunto.ReconstruirTenantAsync(tenant.Id, vivos, marco, ct))
            {
                return false;
            }

            sessoes += vivos.Count;
        }

        if (!await conjunto.MarcarReconstruidoAsync(ct))
        {
            return false;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Conjuntos de licença reconstruídos a partir do PostgreSQL: {Sessoes} sessões", sessoes);
        }

        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        conjunto.AoReconectar(() => Interlocked.Exchange(ref _reconexaoPendente, 1));
        using var relogio = new PeriodicTimer(intervalo);
        do
        {
            try
            {
                await VerificarAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The loop is the only trigger of the rebuild; dying here would leave the sets unrebuilt for good.
                Interlocked.Exchange(ref _reconexaoPendente, 1);
                logger.LogError(ex, "Falha na reconstrução dos conjuntos de licença; nova tentativa no próximo ciclo");
            }
        }
        while (await relogio.WaitForNextTickAsync(stoppingToken));
    }

    private async Task VerificarAsync(CancellationToken ct)
    {
        var reconstruido = await conjunto.ReconstruidoAsync(ct);
        if (reconstruido is null)
        {
            return;
        }

        // Consumed only with the Valkey answering, so an outage still going on keeps the request for later.
        if (conjunto.ConsumirIndisponibilidade())
        {
            Interlocked.Exchange(ref _reconexaoPendente, 1);
        }

        if (reconstruido.Value && Interlocked.Exchange(ref _reconexaoPendente, 0) == 0)
        {
            return;
        }

        if (!await ReconstruirAsync(ct))
        {
            Interlocked.Exchange(ref _reconexaoPendente, 1);
        }
    }

    private async Task<List<MembroVivo>> MembrosVivosAsync(AppTenantInfo tenant, CancellationToken ct)
    {
        await using var escopo = escopos.CreateAsyncScope();
        var servicos = escopo.ServiceProvider;
        servicos.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
        var db = servicos.GetRequiredService<PafDbContext>();

        var linhas = await db.Database.SqlQuery<LinhaViva>($"""
            SELECT "SessaoId", "UnidadeId",
                   (extract(epoch FROM "UltimaAtividadeEm" + make_interval(mins => {Licencas.PrazoMarcaMinutos}) - now()) * 1000)::bigint AS "RestanteMs"
              FROM paf."AtividadesSessao"
             WHERE "TenantId" = {tenant.Id}
               AND "UltimaAtividadeEm" > now() - make_interval(mins => {Licencas.PrazoMarcaMinutos})
            """).ToListAsync(ct);

        // A session revoked or expired in the kit (a kit screen revokes without touching the PAF row) stays out.
        var sessoes = servicos.GetRequiredService<ISessionService>();
        var vivos = new List<MembroVivo>(linhas.Count);
        foreach (var linha in linhas)
        {
            if (await sessoes.GetSessionAsync(linha.SessaoId, ct) is { IsActive: true })
            {
                vivos.Add(new MembroVivo(linha.SessaoId, linha.UnidadeId, TimeSpan.FromMilliseconds(linha.RestanteMs)));
            }
        }

        return vivos;
    }

    private sealed record LinhaViva(Guid SessaoId, Guid UnidadeId, long RestanteMs);
}
