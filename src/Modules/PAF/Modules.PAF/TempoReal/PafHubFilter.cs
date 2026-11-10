using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Conferencia;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Sessao;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.TempoReal;

/// <summary>
/// Revalidates the connection on connect and on every client call (seção 7, "Revogar sessão encerra a conexão"):
/// over WebSockets the per-request middlewares run once, at the upgrade, so a revoked link or session would
/// otherwise keep its connection. A station needs a live VinculoEstacao; a person, an active kit session; an
/// impersonation, a live grant with <c>act_tenant</c> of the root tenant.
/// Refusal aborts the connection.
/// </summary>
public sealed partial class PafHubFilter(ILogger<PafHubFilter> logger) : IHubFilter
{
    public const string MensagemRecusada = "Credencial ou sessão encerrada. Conecte novamente.";

    // Grant check (HybridCache, then Valkey or PostgreSQL) on every hub call.
    private static readonly TimeSpan LimiteConcessao = TimeSpan.FromSeconds(5);

    // Context.Items key of a connection whose client left before the check finished.
    private const string AbandonadaNaAbertura = "paf:abandonada-na-abertura";

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        AppTenantInfo tenant;
        try
        {
            tenant = await ConferirAsync(context.Context, context.ServiceProvider).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.Context.ConnectionAborted.IsCancellationRequested)
        {
            // The client left mid-connect (closed tab, navigation): a cancellation let out of OnConnectedAsync is
            // logged by SignalR as an error. Returning instead makes SignalR run OnDisconnectedAsync, which it skips
            // when OnConnectedAsync throws; the mark keeps PafHub.OnDisconnectedAsync from acting on a credential
            // that was never fully checked (a station would get a drop recorded).
            context.Context.Items[AbandonadaNaAbertura] = true;
            LogSaiuNaAbertura(logger, context.Context.ConnectionId);
            return;
        }

        // Set here, not inside ConferirAsync: an AsyncLocal set in an awaited method does not flow back to its caller.
        InstalarTenant(context.ServiceProvider, tenant);
        await next(context).ConfigureAwait(false);
    }

    public Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        return context.Context.Items.ContainsKey(AbandonadaNaAbertura) ? Task.CompletedTask : next(context, exception);
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        ArgumentNullException.ThrowIfNull(invocationContext);
        ArgumentNullException.ThrowIfNull(next);
        AppTenantInfo tenant;
        try
        {
            tenant = await ConferirAsync(invocationContext.Context, invocationContext.ServiceProvider).ConfigureAwait(false);
        }
        catch (LicencaRecusadaException)
        {
            // On a call the connection stays open unless closed here. On connect the exception out of OnConnectedAsync
            // already ends the connection, and SignalR is expected to send it as the close reason, which an Abort
            // before it could race away (not exercised: in one instance the middleware refuses first).
            invocationContext.Context.Abort();
            throw;
        }

        InstalarTenant(invocationContext.ServiceProvider, tenant);
        try
        {
            return await next(invocationContext).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (invocationContext.Context.ConnectionAborted.IsCancellationRequested)
        {
            // The client left mid-call (closed tab, navigation): SignalR cancels the call and would log the
            // cancellation as an error. Nobody is left to receive the result.
            LogSaiuNaChamada(logger, invocationContext.Context.ConnectionId, invocationContext.HubMethodName);
            return null;
        }
    }

    // The hub scope does not reliably carry the tenant of the request that opened the connection (long polling
    // runs it detached), and Finbuckle's DbContexts read the tenant when they are built: install it from the
    // token before anything resolves a DbContext. Synchronous so the AsyncLocal flows back to the caller.
    private static void InstalarTenant(IServiceProvider servicos, AppTenantInfo tenant) =>
        servicos.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);

    private async Task<AppTenantInfo> ConferirAsync(HubCallerContext contexto, IServiceProvider servicos)
    {
        var ct = contexto.ConnectionAborted;
        var conexao = ConexaoPaf.Ler(contexto.User);
        var tenant = conexao is null
            ? null
            : await servicos.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(conexao.TenantId).ConfigureAwait(false);
        if (conexao is null || tenant is null)
        {
            throw Recusar(contexto, "principal");
        }

        InstalarTenant(servicos, tenant);
        var vigente = conexao switch
        {
            ConexaoEstacao estacao => await EstacaoVigenteAsync(servicos, estacao, ct).ConfigureAwait(false),
            // Before the session arm: an impersonation is judged by its grant, whatever else the token carries.
            ConexaoPessoa { Impersonacao: true } => await ImpersonacaoVigenteAsync(servicos, contexto, ct).ConfigureAwait(false),
            ConexaoPessoa { SessaoId: { } sessaoId } pessoa => await SessaoVigenteAsync(servicos, contexto, pessoa, sessaoId, ct).ConfigureAwait(false),
            // The root token without sid has no kit session to re-check; the expiry of the token closes it
            // (CloseOnAuthenticationExpiration), as SessionCheckMiddleware lets it through.
            _ => true,
        };

        if (!vigente)
        {
            throw Recusar(contexto, conexao.GetType().Name);
        }

        return tenant;
    }

    // Over WebSockets the kit's JWT hook checks the grant once, at the upgrade: revocation or end of the grant
    // must also drop a connection that is already open (RF22, RNF15). Unknown grant counts as ended (kit).
    private static async Task<bool> ImpersonacaoVigenteAsync(IServiceProvider servicos, HubCallerContext contexto, CancellationToken ct)
    {
        var principal = contexto.User!;
        var jti = principal.FindFirstValue(JwtRegisteredClaimNames.Jti);
        if (!ImpersonacaoPaf.DaCodefi(principal) || string.IsNullOrEmpty(jti))
        {
            return false;
        }

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(LimiteConcessao);
        return !await servicos.GetRequiredService<IImpersonationGrantService>()
            .IsRevokedOrEndedAsync(jti, limite.Token).ConfigureAwait(false);
    }

    // Active kit session, then the licence: a reconnection or a call that arrives after the grace released the seat
    // re-occupies through the cascade, or is refused with the cause (ARQ seção 9). The root tenant takes no licence.
    private async Task<bool> SessaoVigenteAsync(
        IServiceProvider servicos, HubCallerContext contexto, ConexaoPessoa pessoa, Guid sessaoId, CancellationToken ct)
    {
        if (await servicos.GetRequiredService<ISessionService>().GetSessionAsync(sessaoId, ct).ConfigureAwait(false) is not { IsActive: true } sessao)
        {
            return false;
        }

        if (string.Equals(pessoa.TenantId, MultitenancyConstants.Root.Id, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            await servicos.GetRequiredService<ReocupacaoSessao>()
                .GarantirAsync(pessoa.TenantId, sessaoId, Guid.Parse(sessao.UserId!), ct).ConfigureAwait(false);
            return true;
        }
        catch (UnauthorizedException ex)
        {
            LogLicencaRecusada(logger, contexto.ConnectionId);
            throw new LicencaRecusadaException(ex.Message);
        }
    }

    private static Task<bool> EstacaoVigenteAsync(IServiceProvider servicos, ConexaoEstacao estacao, CancellationToken ct)
    {
        // Estacoes is unit-filtered and fails closed; the station's unit is fixed by its credential (ADR 0006).
        servicos.GetRequiredService<IUnidadeCorrente>().Definir(estacao.UnidadeId);
        var db = servicos.GetRequiredService<PafDbContext>();
        return CredencialEstacao.VigenteAsync(db, estacao.EstacaoId, estacao.VinculoId, estacao.Papel, ct);
    }

    private CredencialRecusadaException Recusar(HubCallerContext contexto, string tipo)
    {
        LogConexaoRecusada(logger, contexto.ConnectionId, tipo);
        contexto.Abort();
        return new CredencialRecusadaException(MensagemRecusada);
    }

    /// <summary>Credential revoked, session ended or principal invalid, on connect or on a call (event 9003).</summary>
    public sealed class CredencialRecusadaException : HubException
    {
        public CredencialRecusadaException()
        {
        }

        public CredencialRecusadaException(string message)
            : base(message)
        {
        }

        public CredencialRecusadaException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>Licence refused on connect or on a call; the message is the cause the screen shows (ARQ seção 9).</summary>
    public sealed class LicencaRecusadaException : HubException
    {
        public LicencaRecusadaException()
        {
        }

        public LicencaRecusadaException(string message)
            : base(message)
        {
        }

        public LicencaRecusadaException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    [LoggerMessage(EventId = 9006, Level = LogLevel.Information,
        Message = "PafHub recusou a conexao {ConnectionId}: licenca da sessao recusada na reocupacao")]
    private static partial void LogLicencaRecusada(ILogger logger, string connectionId);

    [LoggerMessage(EventId = 9005, Level = LogLevel.Debug,
        Message = "PafHub: o cliente saiu durante a abertura da conexao {ConnectionId}")]
    private static partial void LogSaiuNaAbertura(ILogger logger, string connectionId);

    [LoggerMessage(EventId = 9007, Level = LogLevel.Debug,
        Message = "PafHub: o cliente saiu durante a chamada {Metodo} da conexao {ConnectionId}")]
    private static partial void LogSaiuNaChamada(ILogger logger, string connectionId, string metodo);

    [LoggerMessage(EventId = 9003, Level = LogLevel.Information,
        Message = "PafHub recusou a conexao {ConnectionId} ({Tipo}): credencial revogada, sessao encerrada ou principal invalido")]
    private static partial void LogConexaoRecusada(ILogger logger, string connectionId, string tipo);
}
