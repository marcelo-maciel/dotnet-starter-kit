using System.Security.Claims;
using FSH.Modules.PAF.TempoReal;
using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Cliente que sai no meio da abertura da conexão ao PafHub (aba fechada, navegação): a conferência da sessão é
/// cancelada pelo <c>ConnectionAborted</c>, e o filtro encerra sem exceção e sem seguir para o hub. Uma exceção que
/// sai de <c>OnConnectedAsync</c> o SignalR registra como erro ("Error when dispatching OnConnectedAsync"), o que
/// em produção virava ruído a cada aba fechada durante o handshake. Sem a exceção, o SignalR chama
/// <c>OnDisconnectedAsync</c> (que ele pula quando a abertura lança), e o filtro também não segue para o hub ali: a
/// credencial não foi conferida até o fim, e o hub gravaria a queda de uma estação. O controle, com a conexão viva,
/// prova que o mesmo usuário passa pelo filtro nos dois eventos, então o silêncio do primeiro caso vem do
/// cancelamento e não de uma recusa.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafHubFilterConexaoAbortadaTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private readonly PafTenantCliente _tenant = new(factory, "paf-ab");

    public ValueTask InitializeAsync() => new(_tenant.InicializarAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task ClienteSaiNoMeioDaConexao_FiltroEncerraSemExcecaoESemSeguirParaOHub()
    {
        var token = await TokenDeAtendenteAsync();
        using var abortada = new CancellationTokenSource();
        await abortada.CancelAsync();

        var resultado = await ConectarEDesconectarNoFiltroAsync(Principal(token), abortada.Token);

        resultado.Erro.ShouldBeNull();
        resultado.SeguiuNaAbertura.ShouldBeFalse("a conexão já caiu: não há grupo nem registro a montar");
        resultado.SeguiuNaQueda.ShouldBeFalse("credencial não conferida: o hub não registra queda");
    }

    [Fact]
    public async Task ConexaoViva_FiltroSegueParaOHubNaAberturaENaQueda()
    {
        var token = await TokenDeAtendenteAsync();

        var resultado = await ConectarEDesconectarNoFiltroAsync(Principal(token), CancellationToken.None);

        resultado.Erro.ShouldBeNull();
        resultado.SeguiuNaAbertura.ShouldBeTrue();
        resultado.SeguiuNaQueda.ShouldBeTrue();
    }

    // A call cut short by the client leaving: SignalR cancels it through ConnectionAborted and logs the cancellation
    // as an error ("Failed to invoke hub method"), seen in production with EntrarNoGuiche on 10/11/2026. Nobody is
    // left to receive the result, so the filter ends the call quietly.
    [Fact]
    public async Task ClienteSaiNoMeioDaChamada_FiltroEncerraSemExcecao()
    {
        var token = await TokenDeAtendenteAsync();
        using var conexao = new CancellationTokenSource();

        var resultado = await InvocarNoFiltroAsync(Principal(token), conexao, saiDuranteAChamada: true);

        resultado.Erro.ShouldBeNull();
        resultado.Seguiu.ShouldBeTrue("a conferência passou e a chamada começou antes de o cliente sair");
    }

    // Control: the same cancellation with the connection still open is not the client leaving, so it propagates.
    [Fact]
    public async Task ChamadaCanceladaComConexaoViva_ExcecaoSegue()
    {
        var token = await TokenDeAtendenteAsync();
        using var conexao = new CancellationTokenSource();

        var resultado = await InvocarNoFiltroAsync(Principal(token), conexao, saiDuranteAChamada: false);

        resultado.Erro.ShouldBeOfType<OperationCanceledException>();
    }

    private async Task<(bool Seguiu, Exception? Erro)> InvocarNoFiltroAsync(
        ClaimsPrincipal principal, CancellationTokenSource conexao, bool saiDuranteAChamada)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var servicos = scope.ServiceProvider;
        var contexto = new ContextoDeTeste(principal, conexao.Token);
        using var hub = new PafHub(servicos.GetRequiredService<ConexoesPaf>(), servicos, NullLogger<PafHub>.Instance);
        var filtro = servicos.GetRequiredService<PafHubFilter>();
        var invocacao = new HubInvocationContext(contexto, servicos, hub, typeof(PafHub).GetMethod(nameof(PafHub.EntrarNoGuiche))!, []);
        var seguiu = false;
        try
        {
            await filtro.InvokeMethodAsync(invocacao, async _ =>
            {
                seguiu = true;
                if (saiDuranteAChamada)
                {
                    await conexao.CancelAsync();
                }

                throw new OperationCanceledException(saiDuranteAChamada ? conexao.Token : CancellationToken.None);
            });
            return (seguiu, null);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HubException)
        {
            return (seguiu, ex);
        }
    }

    private async Task<string> TokenDeAtendenteAsync()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        return (await _tenant.EntrarAsync(usuario)).AccessToken;
    }

    // Called directly, as in PafReocupacaoLicencaTests: a real client cannot time its exit to land inside the check.
    // Both events on the same caller context, as SignalR does for one connection.
    private async Task<(bool SeguiuNaAbertura, bool SeguiuNaQueda, Exception? Erro)> ConectarEDesconectarNoFiltroAsync(
        ClaimsPrincipal principal, CancellationToken conexaoAbortada)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var servicos = scope.ServiceProvider;
        var contexto = new ContextoDeTeste(principal, conexaoAbortada);
        using var hub = new PafHub(servicos.GetRequiredService<ConexoesPaf>(), servicos, NullLogger<PafHub>.Instance);
        var filtro = servicos.GetRequiredService<PafHubFilter>();
        var vida = new HubLifetimeContext(contexto, servicos, hub);
        var seguiuNaAbertura = false;
        var seguiuNaQueda = false;
        try
        {
            await filtro.OnConnectedAsync(vida, _ =>
            {
                seguiuNaAbertura = true;
                return Task.CompletedTask;
            });
            await filtro.OnDisconnectedAsync(vida, null, (_, _) =>
            {
                seguiuNaQueda = true;
                return Task.CompletedTask;
            });
            return (seguiuNaAbertura, seguiuNaQueda, null);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HubException)
        {
            return (seguiuNaAbertura, seguiuNaQueda, ex);
        }
    }

    // The principal the JwtBearer handler would build: the token's claims, unvalidated, under an authenticated identity.
    private static ClaimsPrincipal Principal(string token) =>
        new(new ClaimsIdentity(
            new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token).Claims,
            authenticationType: "Bearer"));

    private sealed class ContextoDeTeste(ClaimsPrincipal principal, CancellationToken conexaoAbortada) : HubCallerContext
    {
        public override string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => principal;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => conexaoAbortada;
        public override void Abort()
        {
        }
    }
}
