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
/// em produção virava ruído a cada aba fechada durante o handshake. O controle, com a conexão viva, prova que o
/// mesmo usuário passa pelo filtro, então o silêncio do primeiro caso vem do cancelamento e não de uma recusa.
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

        var (seguiu, erro) = await ConectarNoFiltroAsync(Principal(token), abortada.Token);

        erro.ShouldBeNull();
        seguiu.ShouldBeFalse("a conexão já caiu: não há grupo nem registro a montar");
    }

    [Fact]
    public async Task ConexaoViva_FiltroSegueParaOHub()
    {
        var token = await TokenDeAtendenteAsync();

        var (seguiu, erro) = await ConectarNoFiltroAsync(Principal(token), CancellationToken.None);

        erro.ShouldBeNull();
        seguiu.ShouldBeTrue();
    }

    private async Task<string> TokenDeAtendenteAsync()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        return (await _tenant.EntrarAsync(usuario)).AccessToken;
    }

    // Called directly, as in PafReocupacaoLicencaTests: a real client cannot time its exit to land inside the check.
    private async Task<(bool Seguiu, Exception? Erro)> ConectarNoFiltroAsync(ClaimsPrincipal principal, CancellationToken conexaoAbortada)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var servicos = scope.ServiceProvider;
        var contexto = new ContextoDeTeste(principal, conexaoAbortada);
        using var hub = new PafHub(servicos.GetRequiredService<ConexoesPaf>(), servicos, NullLogger<PafHub>.Instance);
        var seguiu = false;
        try
        {
            await servicos.GetRequiredService<PafHubFilter>().OnConnectedAsync(
                new HubLifetimeContext(contexto, servicos, hub),
                _ =>
                {
                    seguiu = true;
                    return Task.CompletedTask;
                });
            return (seguiu, null);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HubException)
        {
            return (seguiu, ex);
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
