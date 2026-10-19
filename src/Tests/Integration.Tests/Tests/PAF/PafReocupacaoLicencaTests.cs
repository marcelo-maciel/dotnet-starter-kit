using System.Security.Claims;
using FSH.Framework.Eventing.Persistence;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Ocupação na requisição e na conexão ao PafHub (S06, ARQ seção 9 "sessão revivida"): o token de acesso ainda válido
/// que chega depois de a licença ter sido solta passa pela cascata do login, sem renovar o token, e a recusa é 401 com
/// a causa (REST e abertura da conexão) ou <see cref="HubException"/> com a causa (filtro do hub). O harness sobe sem
/// Valkey, então a decisão é a do modo degradado; a soltura pela carência é a de <see cref="Licencas.LiberarAsync"/>,
/// que é o que <c>RenovacaoPelaConexao</c> chama depois da carência (só com Valkey).
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafReocupacaoLicencaTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Perfil = $"{TestConstants.IdentityBasePath}/profile";
    private readonly PafTenantCliente _tenant = new(factory, "paf-ro");
    private readonly List<HubConnection> _conexoes = [];

    public Task InitializeAsync() => _tenant.InicializarAsync();

    public async Task DisposeAsync()
    {
        foreach (var conexao in _conexoes)
        {
            await conexao.DisposeAsync();
        }
    }

    [Fact]
    public async Task WifiForaAlemDaCarencia_VagaTomada_ReconexaoRecusadaERest401ComAMensagemDeLimite()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var ana = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        var tokenAna = (await _tenant.EntrarAsync(ana)).AccessToken;
        await (await ConectarAsync(tokenAna)).StopAsync();
        await SoltarPelaCarenciaAsync(tokenAna);
        await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(unidade.UnidadeId));

        var reconexao = await Should.ThrowAsync<HttpRequestException>(() => ConectarAsync(tokenAna));
        using var cliente = _tenant.Cliente(tokenAna);
        using var resposta = await cliente.GetAsync(Perfil);

        reconexao.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        resposta.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        PafSessaoLicencaTests.Detalhe(await resposta.Content.ReadAsStringAsync()).ShouldBe(Licencas.MensagemLimite);
        (await _tenant.AtividadesAsync(Guid.Parse(ana.UserId))).ShouldBeEmpty("recusada, a sessão não ocupa nada");
    }

    [Fact]
    public async Task WifiForaAlemDaCarencia_VagaLivre_ReocupaComOMesmoToken()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var ana = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        var tokenAna = (await _tenant.EntrarAsync(ana)).AccessToken;
        await SoltarPelaCarenciaAsync(tokenAna);

        using var cliente = _tenant.Cliente(tokenAna);
        using var resposta = await cliente.GetAsync(Perfil);

        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        var atividade = (await _tenant.AtividadesAsync(Guid.Parse(ana.UserId))).ShouldHaveSingleItem();
        atividade.SessaoId.ShouldBe(PafTenantCliente.SessaoDoToken(tokenAna));
        atividade.UnidadeId.ShouldBe(unidade.UnidadeId);
        (await ConectarAsync(tokenAna)).State.ShouldBe(HubConnectionState.Connected);

        // Occupied again, the seat is the session's: a second user in the limit-1 unit is refused.
        var outro = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        (await _tenant.LoginAsync(outro.Email, outro.Password)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // A ocupa outra pessoa; a cascata leva a sessão para B, e cada ocupação que mudasse a unidade gravaria um aviso.
    // Os pedidos esperam um pelo outro no bloqueio por usuário; quem chega depois acha a marca já viva e não ocupa.
    [Fact]
    public async Task RestEReconexaoSimultaneas_UmaOcupacao()
    {
        var a = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var b = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var usuario = await _tenant.CriarUsuarioAsync(a.UnidadeId, b.UnidadeId);
        var token = (await _tenant.EntrarAsync(usuario)).AccessToken;
        // Expired mark, row kept: its unit is what the screen showed, so moving to B is noticed (the grace release
        // deletes the row, and without Valkey state there is then no earlier unit to compare with).
        await EnvelhecerMarcaAsync(token);
        await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(a.UnidadeId));

        using var cliente = _tenant.Cliente(token);
        var rest = Enumerable.Range(0, 4).Select(_ => cliente.GetAsync(Perfil)).ToList();
        var hub = ConectarAsync(token);
        var respostas = await Task.WhenAll(rest);
        var conexao = await hub;

        foreach (var resposta in respostas)
        {
            resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
            resposta.Dispose();
        }

        conexao.State.ShouldBe(HubConnectionState.Connected);
        (await _tenant.AtividadesAsync(Guid.Parse(usuario.UserId))).ShouldHaveSingleItem().UnidadeId.ShouldBe(b.UnidadeId);
        (await AvisosDeUnidadeAsync(usuario.UserId)).ShouldBe(1, "uma ocupação, um aviso de unidade alterada");
    }

    [Fact]
    public async Task Filtro_ChamadaComMarcaVencidaESemSaldo_HubExceptionEAbort()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var ana = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        var tokenAna = (await _tenant.EntrarAsync(ana)).AccessToken;
        await EnvelhecerMarcaAsync(tokenAna);
        await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(unidade.UnidadeId));

        var chamada = await InvocarFiltroAsync(Principal(tokenAna));
        var abertura = await ConectarNoFiltroAsync(Principal(tokenAna));

        chamada.Erro.ShouldBeAssignableTo<HubException>()!.Message.ShouldBe(Licencas.MensagemLimite);
        chamada.Abortada.ShouldBeTrue("a chamada recusada derruba a conexão");
        // On connect SignalR closes the connection itself with the exception as the close reason (no reconnect).
        abertura.Erro.ShouldBeAssignableTo<HubException>()!.Message.ShouldBe(Licencas.MensagemLimite);
        (await _tenant.AtividadesAsync(Guid.Parse(ana.UserId))).ShouldHaveSingleItem().UltimaAtividadeEm
            .ShouldBeLessThan(DateTime.UtcNow.AddMinutes(-10), "recusada, a marca vencida não é renovada");
    }

    [Fact]
    public async Task Filtro_ChamadaComMarcaVencidaEComSaldo_ReocupaEPassa()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var ana = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        var tokenAna = (await _tenant.EntrarAsync(ana)).AccessToken;
        await EnvelhecerMarcaAsync(tokenAna);

        var chamada = await InvocarFiltroAsync(Principal(tokenAna));

        chamada.Erro.ShouldBeNull();
        chamada.Abortada.ShouldBeFalse();
        (await _tenant.AtividadesAsync(Guid.Parse(ana.UserId))).ShouldHaveSingleItem().UltimaAtividadeEm
            .ShouldBeGreaterThan(DateTime.UtcNow.AddMinutes(-1), "a chamada reocupou a licença");
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private async Task<HubConnection> ConectarAsync(string token)
    {
        var conexao = await new PafOperacao(factory, _tenant).ConectarAsync(token);
        _conexoes.Add(conexao);
        return conexao;
    }

    // What RenovacaoPelaConexao does once the last connection has been gone longer than the grace.
    private async Task SoltarPelaCarenciaAsync(string token)
    {
        using var scope = factory.Services.CreateScope();
        _tenant.Contexto(scope, unidadeCorrente: null);
        await scope.ServiceProvider.GetRequiredService<Licencas>()
            .LiberarAsync(_tenant.TenantId, PafTenantCliente.SessaoDoToken(token), CancellationToken.None);
    }

    private async Task EnvelhecerMarcaAsync(string token)
    {
        var sessaoId = PafTenantCliente.SessaoDoToken(token);
        (await _tenant.ExecutarAsync($"""
            UPDATE paf."AtividadesSessao" SET "UltimaAtividadeEm" = now() - interval '11 minutes' WHERE "SessaoId" = {sessaoId}
            """)).ShouldBe(1);
    }

    // Outbox rows of the unit-change notice addressed to one user, processed or not.
    private async Task<int> AvisosDeUnidadeAsync(string usuarioId)
    {
        using var scope = factory.Services.CreateScope();
        var eventing = scope.ServiceProvider.GetRequiredService<EventingDbContext>();
        return await eventing.OutboxMessages.CountAsync(
            m => m.Type.Contains(nameof(UnidadeCorrenteAlteradaPaf)) && m.Payload.Contains(usuarioId));
    }

    // The principal the JwtBearer handler would build: the token's claims, unvalidated, under an authenticated identity.
    private static ClaimsPrincipal Principal(string token) =>
        new(new ClaimsIdentity(
            new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token).Claims,
            authenticationType: "Bearer"));

    // Called directly, as in PafHubConexoesTests: over long polling every call is an HTTP request that the session
    // middleware checks first, which would hide the filter's own check.
    private async Task<(bool Abortada, Exception? Erro)> InvocarFiltroAsync(ClaimsPrincipal principal)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var servicos = scope.ServiceProvider;
        var contexto = new ContextoDeTeste(principal);
        using var hub = new PafHub(servicos.GetRequiredService<ConexoesPaf>(), servicos, NullLogger<PafHub>.Instance);
        var invocacao = new HubInvocationContext(contexto, servicos, hub, typeof(PafHub).GetMethod(nameof(PafHub.EntrarNoGuiche))!, []);
        try
        {
            await servicos.GetRequiredService<PafHubFilter>().InvokeMethodAsync(invocacao, _ => ValueTask.FromResult<object?>(null));
            return (contexto.Abortada, null);
        }
        catch (HubException ex)
        {
            return (contexto.Abortada, ex);
        }
    }

    private async Task<(bool Abortada, Exception? Erro)> ConectarNoFiltroAsync(ClaimsPrincipal principal)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var servicos = scope.ServiceProvider;
        var contexto = new ContextoDeTeste(principal);
        using var hub = new PafHub(servicos.GetRequiredService<ConexoesPaf>(), servicos, NullLogger<PafHub>.Instance);
        try
        {
            await servicos.GetRequiredService<PafHubFilter>().OnConnectedAsync(
                new HubLifetimeContext(contexto, servicos, hub), _ => Task.CompletedTask);
            return (contexto.Abortada, null);
        }
        catch (HubException ex)
        {
            return (contexto.Abortada, ex);
        }
    }

    private sealed class ContextoDeTeste(ClaimsPrincipal principal) : HubCallerContext
    {
        public bool Abortada { get; private set; }
        public override string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => principal;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() => Abortada = true;
    }
}
