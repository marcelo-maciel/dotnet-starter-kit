using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Sessao;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
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

    public override async Task OnConnectedAsync()
    {
        // Context.User, never ICurrentUser: the negotiate HttpContext is not pinned to the connection.
        var conexao = ConexaoPaf.Ler(Context.User);
        try
        {
            if (conexao is not null)
            {
                conexoes.Registrar(Context, conexao);
            }

            switch (conexao)
            {
                case ConexaoEstacao estacao:
                    await EntrarAsync(PafGrupos.Unidade(estacao.TenantId, estacao.UnidadeId)).ConfigureAwait(false);
                    if (estacao.Papel == PafPapeisEstacao.Painel)
                    {
                        await EntrarAsync(PafGrupos.Painel(estacao.TenantId, estacao.EstacaoId)).ConfigureAwait(false);
                    }

                    LogEstacaoConectada(logger, Context.ConnectionId, estacao.TenantId, estacao.UnidadeId, estacao.EstacaoId, estacao.Papel);
                    break;

                // The impersonation token shares the user's sub; in the user group it would receive the
                // real user's unit-switch notice (seção 7, RF22), so it stays out.
                case ConexaoPessoa { Impersonacao: false } pessoa:
                    await EntrarAsync(PafGrupos.Usuario(pessoa.TenantId, pessoa.UsuarioId)).ConfigureAwait(false);
                    // The guichê may already be selected (reload, second window): the group follows SessaoEstacao.
                    if (pessoa.SessaoId is { } sessaoId
                        && await GuicheDaSessaoAsync(sessaoId).ConfigureAwait(false) is { } guicheId)
                    {
                        await conexoes.MoverConexaoParaGuicheAsync(Context.ConnectionId, pessoa.TenantId, guicheId, Context.ConnectionAborted)
                            .ConfigureAwait(false);
                    }

                    LogPessoaConectada(logger, Context.ConnectionId, pessoa.TenantId, pessoa.UsuarioId);
                    break;

                case ConexaoPessoa pessoa:
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

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        conexoes.Remover(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Called by the guichê screen after it selects or changes its guichê, and after every reconnect: the server
    /// reads the session's active SessaoEstacao and moves the session's connections to that guichê's group. The
    /// client names nothing (seção 7). Returns the guichê id, or null when the session holds none.
    /// </summary>
    public async Task<Guid?> EntrarNoGuiche()
    {
        // Impersonation has no kit session to read SessaoEstacao by; its guichê group is not covered yet.
        if (ConexaoPaf.Ler(Context.User) is not ConexaoPessoa { Impersonacao: false, SessaoId: { } sessaoId } pessoa)
        {
            throw new HubException("Só a sessão de um atendente tem guichê.");
        }

        var guicheId = await GuicheDaSessaoAsync(sessaoId).ConfigureAwait(false);
        await conexoes.MoverSessaoParaGuicheAsync(pessoa.TenantId, sessaoId, guicheId, Context.ConnectionAborted).ConfigureAwait(false);
        return guicheId;
    }

    // Resolved here, after PafHubFilter installed the tenant: the DbContext reads it when built.
    private async Task<Guid?> GuicheDaSessaoAsync(Guid sessaoId)
    {
        var db = servicos.GetRequiredService<PafDbContext>();
        var vinculo = await VinculosGuiche.AtivoDaSessaoAsync(db, sessaoId, Context.ConnectionAborted).ConfigureAwait(false);
        return vinculo?.EstacaoId;
    }

    private Task EntrarAsync(string grupo) =>
        Groups.AddToGroupAsync(Context.ConnectionId, grupo, Context.ConnectionAborted);

    [LoggerMessage(EventId = 9001, Level = LogLevel.Information,
        Message = "PafHub estacao conectada {ConnectionId} tenant={TenantId} unidade={UnidadeId} estacao={EstacaoId} papel={Papel}")]
    private static partial void LogEstacaoConectada(ILogger logger, string connectionId, string tenantId, Guid unidadeId, Guid estacaoId, string papel);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Information,
        Message = "PafHub pessoa conectada {ConnectionId} tenant={TenantId} usuario={UsuarioId}")]
    private static partial void LogPessoaConectada(ILogger logger, string connectionId, string tenantId, string usuarioId);
}
