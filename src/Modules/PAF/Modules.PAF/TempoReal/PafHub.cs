using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Sessao;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.TempoReal;

/// <summary>
/// PAF's own hub (seção 7): AppHub joins chat groups on connect and extending it would be a kit edit
/// outside ADR 0008. Server-to-client, plus <see cref="EntrarNoGuiche"/> for the guichê screen. Every connect
/// and call passes <see cref="PafHubFilter"/> first, which also installs the token's tenant.
/// </summary>
[Authorize(Policy = Politica)]
public sealed partial class PafHub(ConexoesPaf conexoes, IServiceProvider servicos, ILogger<PafHub> logger) : Hub
{
    /// <summary>Accepts a well-formed station credential or a person token; anything else is 403 at negotiate.</summary>
    public const string Politica = "PafHub";

    private static readonly TimeSpan LimiteDesconexao = TimeSpan.FromSeconds(5);

    public override async Task OnConnectedAsync()
    {
        // Context.User, never ICurrentUser: the negotiate HttpContext is not pinned to the connection.
        var conexao = ConexaoPaf.Ler(Context.User);
        try
        {
            if (conexao is not null)
            {
                await conexoes.RegistrarAsync(Context, conexao, Context.ConnectionAborted).ConfigureAwait(false);
            }

            switch (conexao)
            {
                case ConexaoEstacao estacao:
                    await EntrarAsync(estacao.TenantId, PafGrupos.Unidade(estacao.TenantId, estacao.UnidadeId)).ConfigureAwait(false);
                    if (estacao.Papel == PafPapeisEstacao.Painel)
                    {
                        await EntrarAsync(estacao.TenantId, PafGrupos.Painel(estacao.TenantId, estacao.EstacaoId)).ConfigureAwait(false);
                    }

                    LogEstacaoConectada(logger, Context.ConnectionId, estacao.TenantId, estacao.UnidadeId, estacao.EstacaoId, estacao.Papel);

                    // RNF20: after the registry write, so a quick reconnection is already visible to the old one's drop.
                    await servicos.GetRequiredService<MonitoramentoEstacoes>().RegistrarConexaoAsync(estacao, Context.ConnectionAborted).ConfigureAwait(false);
                    break;

                // The impersonation token shares the user's sub; in the user group it would receive the
                // real user's unit-switch notice (seção 7, RF22), so it stays out.
                case ConexaoPessoa { Impersonacao: false } pessoa:
                    await EntrarAsync(pessoa.TenantId, PafGrupos.Usuario(pessoa.TenantId, pessoa.UsuarioId)).ConfigureAwait(false);
                    if (pessoa.SessaoId is { } sessaoId)
                    {
                        // The unit comes from the session state on the server, never from a claim (seção 7, RF08).
                        if (await UnidadeDaSessaoAsync(pessoa.TenantId, sessaoId).ConfigureAwait(false) is { } unidadeId)
                        {
                            await EntrarAsync(pessoa.TenantId, PafGrupos.Unidade(pessoa.TenantId, unidadeId)).ConfigureAwait(false);
                        }

                        // The guichê may already be selected (reload, second window): the group follows SessaoEstacao.
                        if (await GuicheDaSessaoAsync(sessaoId).ConfigureAwait(false) is { } guicheId)
                        {
                            await conexoes.MoverConexaoParaGuicheAsync(Context.ConnectionId, pessoa.TenantId, guicheId, Context.ConnectionAborted)
                                .ConfigureAwait(false);
                        }
                    }

                    LogPessoaConectada(logger, Context.ConnectionId, pessoa.TenantId, pessoa.UsuarioId);
                    break;

                // The grant's own unit (S10B), never the user group: the support's switch must not reach the user.
                case ConexaoPessoa pessoa:
                    if (await UnidadeDaConcessaoAsync().ConfigureAwait(false) is { } unidadeImpersonada)
                    {
                        await EntrarAsync(pessoa.TenantId, PafGrupos.Unidade(pessoa.TenantId, unidadeImpersonada)).ConfigureAwait(false);
                    }

                    // S11: the grant's own guichê link, as the session's above (reload, second window).
                    if (await GuicheDaConcessaoAsync().ConfigureAwait(false) is { } guicheDaConcessao)
                    {
                        await conexoes.MoverConexaoParaGuicheAsync(Context.ConnectionId, pessoa.TenantId, guicheDaConcessao, Context.ConnectionAborted)
                            .ConfigureAwait(false);
                    }

                    LogPessoaConectada(logger, Context.ConnectionId, pessoa.TenantId, pessoa.UsuarioId);
                    break;

                default:
                    // The policy already refuses this at negotiate; kept so a policy change cannot open the hub.
                    Context.Abort();
                    return;
            }

            await base.OnConnectedAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        {
            // Client left mid-connect; there is no connection left to set up.
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // ConnectionAborted is already cancelled here; the registry write gets its own short ceiling.
        using var limite = new CancellationTokenSource(LimiteDesconexao);
        await conexoes.RemoverAsync(Context.ConnectionId, limite.Token).ConfigureAwait(false);

        // RNF20: after the removal, so the check for another live connection of the role no longer sees this one.
        if (ConexaoPaf.Ler(Context.User) is ConexaoEstacao estacao)
        {
            await servicos.GetRequiredService<MonitoramentoEstacoes>().RegistrarQuedaAsync(estacao, limite.Token).ConfigureAwait(false);
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    /// <summary>
    /// Called by the guichê screen after it selects or changes its guichê, and after every reconnect: the server
    /// reads the session's active SessaoEstacao and moves the session's connections to that guichê's group. The
    /// client names nothing (seção 7). Returns the guichê id, or null when the session holds none.
    /// </summary>
    public async Task<Guid?> EntrarNoGuiche()
    {
        // S11: impersonation has no kit session; its guichê is the grant's own SessaoEstacao (ConcessaoId = jti).
        if (ConexaoPaf.Ler(Context.User) is ConexaoPessoa { Impersonacao: true } suporte
            && ConcessaoImpersonacao.DoToken(Context.User) is { } concessao)
        {
            var guicheDaConcessao = await GuicheDaConcessaoAsync().ConfigureAwait(false);
            await conexoes.MoverConcessaoParaGuicheAsync(suporte.TenantId, concessao.ConcessaoId, guicheDaConcessao, Context.ConnectionAborted)
                .ConfigureAwait(false);
            return guicheDaConcessao;
        }

        if (ConexaoPaf.Ler(Context.User) is not ConexaoPessoa { Impersonacao: false, SessaoId: { } sessaoId } pessoa)
        {
            throw new HubException("Só a sessão de um atendente tem guichê.");
        }

        var guicheId = await GuicheDaSessaoAsync(sessaoId).ConfigureAwait(false);
        await conexoes.MoverSessaoParaGuicheAsync(pessoa.TenantId, sessaoId, guicheId, Context.ConnectionAborted).ConfigureAwait(false);
        return guicheId;
    }

    // The grant's active link, null when the token is not a well-formed grant or holds no guichê.
    private async Task<Guid?> GuicheDaConcessaoAsync()
    {
        if (ConcessaoImpersonacao.DoToken(Context.User) is not { } concessao)
        {
            return null;
        }

        var db = servicos.GetRequiredService<PafDbContext>();
        var vinculo = await VinculosGuiche.AtivoDaConcessaoAsync(db, concessao.ConcessaoId, Context.ConnectionAborted).ConfigureAwait(false);
        return vinculo?.EstacaoId;
    }

    // Resolved here, after PafHubFilter installed the tenant: the DbContext reads it when built.
    private async Task<Guid?> GuicheDaSessaoAsync(Guid sessaoId)
    {
        var db = servicos.GetRequiredService<PafDbContext>();
        var vinculo = await VinculosGuiche.AtivoDaSessaoAsync(db, sessaoId, Context.ConnectionAborted).ConfigureAwait(false);
        return vinculo?.EstacaoId;
    }

    // Valkey state first (the unit the session switched to), else the unit of the live licence mark.
    private async Task<Guid?> UnidadeDaSessaoAsync(string tenantId, Guid sessaoId)
    {
        var ct = Context.ConnectionAborted;
        return await servicos.GetRequiredService<EstadoSessao>().LerUnidadeAsync(tenantId, sessaoId, ct).ConfigureAwait(false)
            ?? await servicos.GetRequiredService<Licencas>().UnidadeDaMarcaVivaAsync(tenantId, sessaoId, ct).ConfigureAwait(false);
    }

    // Same resolution as SessionCheckMiddleware: the grant's Valkey key, else its guichê link, the user's live session,
    // the principal unit (seção 9). Null with an empty scope: the connection joins no unit.
    private async Task<Guid?> UnidadeDaConcessaoAsync()
    {
        if (ConcessaoImpersonacao.DoToken(Context.User) is not { } concessao)
        {
            return null;
        }

        return await servicos.GetRequiredService<UnidadeDaConcessao>().ResolverAsync(concessao, Context.ConnectionAborted).ConfigureAwait(false);
    }

    private Task EntrarAsync(string tenantId, string grupo) =>
        conexoes.EntrarNoGrupoAsync(Context.ConnectionId, tenantId, grupo, Context.ConnectionAborted);

    [LoggerMessage(EventId = 9001, Level = LogLevel.Information,
        Message = "PafHub estacao conectada {ConnectionId} tenant={TenantId} unidade={UnidadeId} estacao={EstacaoId} papel={Papel}")]
    private static partial void LogEstacaoConectada(ILogger logger, string connectionId, string tenantId, Guid unidadeId, Guid estacaoId, string papel);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Information,
        Message = "PafHub pessoa conectada {ConnectionId} tenant={TenantId} usuario={UsuarioId}")]
    private static partial void LogPessoaConectada(ILogger logger, string connectionId, string tenantId, string usuarioId);
}
