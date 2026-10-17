using FSH.Modules.PAF.Contracts.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.TempoReal;

/// <summary>
/// PAF's own hub (seção 7): AppHub joins chat groups on connect and extending it would be a kit edit
/// outside ADR 0008. Server-to-client only; the client invokes nothing.
/// </summary>
[Authorize(Policy = Politica)]
public sealed partial class PafHub(ILogger<PafHub> logger) : Hub
{
    /// <summary>Accepts a well-formed station credential or a person token; anything else is 403 at negotiate.</summary>
    public const string Politica = "PafHub";

    public override async Task OnConnectedAsync()
    {
        // Context.User, never ICurrentUser: the negotiate HttpContext is not pinned to the connection.
        var conexao = ConexaoPaf.Ler(Context.User);
        try
        {
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

    private Task EntrarAsync(string grupo) =>
        Groups.AddToGroupAsync(Context.ConnectionId, grupo, Context.ConnectionAborted);

    [LoggerMessage(EventId = 9001, Level = LogLevel.Information,
        Message = "PafHub estacao conectada {ConnectionId} tenant={TenantId} unidade={UnidadeId} estacao={EstacaoId} papel={Papel}")]
    private static partial void LogEstacaoConectada(ILogger logger, string connectionId, string tenantId, Guid unidadeId, Guid estacaoId, string papel);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Information,
        Message = "PafHub pessoa conectada {ConnectionId} tenant={TenantId} usuario={UsuarioId}")]
    private static partial void LogPessoaConectada(ILogger logger, string connectionId, string tenantId, string usuarioId);
}
