using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Sessao;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FSH.Modules.PAF.TempoReal;

/// <summary>
/// Laço por instância da vida da sessão pela conexão (seção 7 e seção 9, decididos em 03/10 e 06/10/2026). A cada
/// ciclo: renova as entradas do registro das conexões que esta instância segura; renova a marca de sessão viva em
/// <c>AtividadeSessao</c> de cada sessão com conexão aqui (no PostgreSQL, com ou sem Valkey), conferindo a cada escrita
/// o <c>UserSession</c> e o usuário; e solta licença e guichê das sessões cuja última conexão caiu há mais que a
/// carência, em qualquer instância. Sem Valkey nada é solto pela carência: esta instância não vê as conexões das
/// outras, e o prazo da marca cobre a sessão abandonada.
/// </summary>
public sealed partial class RenovacaoPelaConexao(
    ConexoesPaf conexoes,
    RegistroConexoes registro,
    IServiceScopeFactory escopos,
    IOptions<OpcoesSessaoPaf> opcoes,
    TimeProvider relogio,
    ILogger<RenovacaoPelaConexao> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var temporizador = new PeriodicTimer(opcoes.Value.Passo, relogio);
        while (await temporizador.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await CicloAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                // Loop boundary: one failed cycle (database or Valkey hiccup) must not stop the next ones.
                LogCicloFalhou(logger, ex);
            }
        }
    }

    /// <summary>One cycle; public so the tests drive it with a simulated clock instead of waiting for the timer.</summary>
    public async Task CicloAsync(CancellationToken ct)
    {
        var locais = conexoes.Locais();
        await registro.RenovarAsync(locais, ct).ConfigureAwait(false);

        var sessoes = locais
            .Select(l => l.Conexao)
            .OfType<ConexaoPessoa>()
            .Where(p => !p.Impersonacao && p.SessaoId is not null)
            .Select(p => (p.TenantId, SessaoId: p.SessaoId!.Value, p.UsuarioId))
            .Distinct()
            .GroupBy(s => s.TenantId, StringComparer.Ordinal);
        foreach (var doTenant in sessoes)
        {
            await RenovarMarcasAsync(doTenant.Key, [.. doTenant.Select(s => (s.SessaoId, s.UsuarioId))], ct).ConfigureAwait(false);
        }

        foreach (var queda in await registro.QuedasVencidasAsync(ct).ConfigureAwait(false))
        {
            await SoltarAsync(queda, ct).ConfigureAwait(false);
        }
    }

    private async Task RenovarMarcasAsync(string tenantId, IReadOnlyList<(Guid SessaoId, string UsuarioId)> sessoes, CancellationToken ct)
    {
        await using var escopo = escopos.CreateAsyncScope();
        var servicos = escopo.ServiceProvider;
        if (await TenantAsync(servicos, tenantId).ConfigureAwait(false) is not { } tenant)
        {
            return;
        }

        Instalar(servicos, tenant);
        var db = servicos.GetRequiredService<PafDbContext>();
        foreach (var (sessaoId, usuarioId) in sessoes)
        {
            // Seam for S09: the renewal of this session's member of the Valkey licence set goes here, next to the mark.
            if (!await RenovarMarcaAsync(db, tenantId, sessaoId, ct).ConfigureAwait(false))
            {
                continue;
            }

            await ConferirSessaoAsync(servicos, tenantId, sessaoId, usuarioId, ct).ConfigureAwait(false);
        }
    }

    // Only a live mark is renewed: a session whose mark expired re-occupies through the login check (seção 9), never
    // by the mere existence of a connection. Spaced by the renewal interval, like the per-request renewal.
    private async Task<bool> RenovarMarcaAsync(PafDbContext db, string tenantId, Guid sessaoId, CancellationToken ct)
    {
        var intervalo = opcoes.Value.IntervaloRenovacao.TotalSeconds;
        var prazo = opcoes.Value.PrazoMarca.TotalSeconds;
        return await db.Database.ExecuteSqlAsync($"""
            UPDATE paf."AtividadesSessao" SET "UltimaAtividadeEm" = now()
             WHERE "TenantId" = {tenantId} AND "SessaoId" = {sessaoId}
               AND "UltimaAtividadeEm" < now() - make_interval(secs => {intervalo})
               AND "UltimaAtividadeEm" > now() - make_interval(secs => {prazo})
            """, ct).ConfigureAwait(false) > 0;
    }

    // The renewal checks the kit session and the user (seção 9, 03/10/2026): a revocation born in the kit whose event
    // was lost, or a user deactivated in a kit screen, stops here within one renewal cycle.
    private async Task ConferirSessaoAsync(IServiceProvider servicos, string tenantId, Guid sessaoId, string usuarioId, CancellationToken ct)
    {
        var sessao = await servicos.GetRequiredService<ISessionService>().GetSessionAsync(sessaoId, ct).ConfigureAwait(false);
        if (sessao is not { IsActive: true })
        {
            await servicos.GetRequiredService<Licencas>().LiberarAsync(tenantId, sessaoId, ct).ConfigureAwait(false);
            await servicos.GetRequiredService<EstadoSessao>().RemoverAsync(tenantId, sessaoId, ct).ConfigureAwait(false);
            await conexoes.EncerrarSessaoAsync(tenantId, sessaoId, ct).ConfigureAwait(false);
            LogSessaoEncerradaNaRenovacao(logger, tenantId, sessaoId);
            return;
        }

        var perfil = await servicos.GetRequiredService<IUserProfileService>().GetAsync(usuarioId, ct).ConfigureAwait(false);
        if (!perfil.IsActive && Guid.TryParse(usuarioId, out var usuario))
        {
            await servicos.GetRequiredService<RevogacaoSessao>()
                .RevogarAsync(tenantId, sessaoId, usuario, RevogacaoSessao.MotivoUsuarioInativo, ct).ConfigureAwait(false);
            LogSessaoEncerradaNaRenovacao(logger, tenantId, sessaoId);
        }
    }

    private async Task SoltarAsync(QuedaSessao queda, CancellationToken ct)
    {
        // Every instance sees the same overdue drop; only the one that removes it releases.
        if (!await registro.ReivindicarQuedaAsync(queda, ct).ConfigureAwait(false))
        {
            return;
        }

        var vivas = await registro.DaSessaoAsync(queda.TenantId, queda.SessaoId, ct).ConfigureAwait(false);
        if (vivas is null)
        {
            await registro.DevolverQuedaAsync(queda, ct).ConfigureAwait(false);
            return;
        }

        // A connection came back on some instance after the drop was recorded: the session keeps licence and guichê.
        if (vivas.Count > 0)
        {
            return;
        }

        try
        {
            await using var escopo = escopos.CreateAsyncScope();
            if (await TenantAsync(escopo.ServiceProvider, queda.TenantId).ConfigureAwait(false) is not { } tenant)
            {
                return;
            }

            Instalar(escopo.ServiceProvider, tenant);

            // Releases the licence (S09 adds its Valkey member to the same call) and the guichê; an atendimento in
            // course stays on the released guichê (RF04).
            await escopo.ServiceProvider.GetRequiredService<Licencas>().LiberarAsync(queda.TenantId, queda.SessaoId, ct).ConfigureAwait(false);
            LogSoltaPelaCarencia(logger, queda.TenantId, queda.SessaoId);
        }
        catch
        {
            await registro.DevolverQuedaAsync(queda, ct).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<AppTenantInfo?> TenantAsync(IServiceProvider servicos, string tenantId)
    {
        var tenant = await servicos.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(tenantId).ConfigureAwait(false);
        if (tenant is null)
        {
            LogTenantAusente(logger, tenantId);
        }

        return tenant;
    }

    // Finbuckle's DbContexts and the kit's Identity services read the tenant when built: installed before any resolve.
    // Synchronous so the AsyncLocal flows back to the caller (an awaited method would drop it on return).
    private static void Instalar(IServiceProvider servicos, AppTenantInfo tenant) =>
        servicos.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);

    [LoggerMessage(EventId = 9012, Level = LogLevel.Error, Message = "PAF: ciclo de renovacao pela conexao falhou")]
    private static partial void LogCicloFalhou(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 9013, Level = LogLevel.Information,
        Message = "PAF: licenca e guiche soltos pela carencia de reconexao tenant={TenantId} sessao={SessaoId}")]
    private static partial void LogSoltaPelaCarencia(ILogger logger, string tenantId, Guid sessaoId);

    [LoggerMessage(EventId = 9014, Level = LogLevel.Information,
        Message = "PAF: sessao revogada ou usuario inativo achado na renovacao pela conexao tenant={TenantId} sessao={SessaoId}")]
    private static partial void LogSessaoEncerradaNaRenovacao(ILogger logger, string tenantId, Guid sessaoId);

    [LoggerMessage(EventId = 9015, Level = LogLevel.Warning, Message = "PAF: tenant {TenantId} ausente no ciclo de renovacao")]
    private static partial void LogTenantAusente(ILogger logger, string tenantId);
}
