using System.Security.Claims;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Integration.Tests.Infrastructure;
using Mediator;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Connection registry and hub filter of PafHub (seção 7): the guichê group follows SessaoEstacao, a revoked
/// station loses its connections, and the filter refuses a dead credential or session on a hub call. The client
/// runs over long polling (see PafHubTests): there every poll and call is an HTTP request that the per-request
/// middlewares already check, so the filter's refusal is exercised by calling it directly.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafHubConexoesTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SilencioEsperado = TimeSpan.FromSeconds(1.5);

    private readonly PafTenantCliente _tenant = new(factory, "paf-cx");
    private readonly List<HubConnection> _conexoes = [];
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public async Task DisposeAsync()
    {
        foreach (var conexao in _conexoes)
        {
            await conexao.DisposeAsync();
        }

        await _op.DrenarCaixaDeSaidaAsync();
    }

    [Fact]
    public async Task Guiche_SelecionadoAntesDeConectar_RecebeSoAChamadaDoProprioGuiche()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendenteA = await _op.AtendenteAsync(c, c.GuicheA);
        using var atendenteB = await _op.AtendenteAsync(c, c.GuicheB);
        var hubA = await ConectarAsync(Token(atendenteA));
        var hubB = await ConectarAsync(Token(atendenteB));
        using var chamadasA = new Caixa<SenhaChamada>(hubA, PafHubMetodos.SenhaChamada);
        using var chamadasB = new Caixa<SenhaChamada>(hubB, PafHubMetodos.SenhaChamada);

        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var chamada = await PafOperacao.ChamarOkAsync(atendenteA);

        var recebida = await chamadasA.EsperarAsync(s => s.AtendimentoId == chamada.AtendimentoId, Espera);
        recebida.ShouldNotBeNull("a conexão da sessão com o guichê A entra no grupo dele ao conectar");
        recebida.EstacaoId.ShouldBe(c.GuicheA);

        await Task.Delay(SilencioEsperado);
        chamadasB.Recebidos.ShouldBeEmpty("a chamada do guichê A não chega ao guichê B");
    }

    [Fact]
    public async Task Guiche_SelecionadoDepoisDeConectar_EntraPeloHub_ESaiDoGuicheAnteriorNaTroca()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        var usuario = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        using var atendente = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
        var hub = await ConectarAsync(Token(atendente));
        using var chamadas = new Caixa<SenhaChamada>(hub, PafHubMetodos.SenhaChamada);

        (await hub.InvokeAsync<Guid?>(PafHubChamadas.EntrarNoGuiche)).ShouldBeNull("sem guichê selecionado não há grupo");

        await SelecionarAsync(atendente, c.GuicheA);
        (await hub.InvokeAsync<Guid?>(PafHubChamadas.EntrarNoGuiche)).ShouldBe(c.GuicheA);
        await SelecionarAsync(atendente, c.GuicheB);
        (await hub.InvokeAsync<Guid?>(PafHubChamadas.EntrarNoGuiche)).ShouldBe(c.GuicheB);

        // Another attendant now takes guichê A: the moved connection must have left A's group.
        using var outroNoA = await _op.AtendenteAsync(c, c.GuicheA);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await PafOperacao.EmitirOkAsync(totem, c.Protocolo, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var chamadaNoA = await PafOperacao.ChamarOkAsync(outroNoA);
        var chamadaNoB = await PafOperacao.ChamarOkAsync(atendente);

        var recebida = await chamadas.EsperarAsync(s => s.AtendimentoId == chamadaNoB.AtendimentoId, Espera);
        recebida.ShouldNotBeNull("a conexão segue o guichê B selecionado depois de conectar");
        recebida.EstacaoId.ShouldBe(c.GuicheB);
        chamadas.Recebidos.ShouldNotContain(s => s.AtendimentoId == chamadaNoA.AtendimentoId, "a troca tirou a conexão do grupo do guichê A");
    }

    [Fact]
    public async Task VinculoRevogado_EncerraAsConexoesDaEstacao_ENaoReconecta()
    {
        var c = await _op.CriarAsync();
        var tokenRevogado = await _op.CredencialAsync(c.UnidadeId, c.Painel, PafPapeisEstacao.Painel);
        var revogado = await ConectarAsync(tokenRevogado);
        var controle = await ConectarAsync(await _op.CredencialAsync(c.UnidadeId, c.PainelProtocolo, PafPapeisEstacao.Painel));
        var fechou = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        revogado.Closed += _ =>
        {
            fechou.TrySetResult();
            return Task.CompletedTask;
        };

        using (var scope = factory.Services.CreateScope())
        {
            _tenant.Contexto(scope, c.UnidadeId);
            await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new RevogarVinculoEstacaoCommand(c.Painel));
        }

        (await Task.WhenAny(fechou.Task, Task.Delay(Espera))).ShouldBe(fechou.Task, "a conexão da estação revogada é encerrada pelo servidor");
        revogado.State.ShouldBe(HubConnectionState.Disconnected);
        controle.State.ShouldBe(HubConnectionState.Connected, "outra estação da unidade continua conectada");
        await Should.ThrowAsync<HttpRequestException>(() => ConectarAsync(tokenRevogado));
    }

    [Fact]
    public async Task RevogacaoSessao_EncerraAsConexoesDaSessao()
    {
        var c = await _op.CriarAsync();
        var usuario = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        var token = (await _tenant.EntrarAsync(usuario)).AccessToken;
        var fechou = Fechamento(await ConectarAsync(token));

        // Called from a scope, not from a request of the revoked session: over long polling that request would
        // be refused and close the client by itself, hiding a missing close on the server.
        using (var scope = factory.Services.CreateScope())
        {
            _tenant.Contexto(scope, unidadeCorrente: null);
            await scope.ServiceProvider.GetRequiredService<RevogacaoSessao>().RevogarAsync(
                _tenant.TenantId, PafTenantCliente.SessaoDoToken(token), Guid.Parse(usuario.UserId), RevogacaoSessao.MotivoUnidadeRetirada, CancellationToken.None);
        }

        (await Task.WhenAny(fechou, Task.Delay(Espera))).ShouldBe(fechou, "a revogação da sessão encerra a conexão dela no hub");
    }

    [Fact]
    public async Task LoginEmOutraMaquina_EncerraAsConexoesDaSessaoAnterior()
    {
        var c = await _op.CriarAsync();
        var usuario = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        var anterior = await ConectarAsync((await _tenant.EntrarAsync(usuario)).AccessToken);
        var fechou = Fechamento(anterior);
        var controle = await ConectarAsync(await _op.CredencialAsync(c.UnidadeId, c.Painel, PafPapeisEstacao.Painel));

        await _tenant.EntrarAsync(usuario);

        (await Task.WhenAny(fechou, Task.Delay(Espera))).ShouldBe(fechou, "a sessão única encerra a conexão da sessão anterior");
        controle.State.ShouldBe(HubConnectionState.Connected, "conexões de outras credenciais seguem abertas");
    }

    [Fact]
    public async Task Filtro_ChamadaComVinculoRevogado_AbortaERecusa_ComVinculoVigentePassa()
    {
        var c = await _op.CriarAsync();
        var token = await _op.CredencialAsync(c.UnidadeId, c.Painel, PafPapeisEstacao.Painel);
        var principal = Principal(token);

        (await InvocarFiltroAsync(principal)).Abortada.ShouldBeFalse("credencial vigente passa pelo filtro");

        using (var scope = factory.Services.CreateScope())
        {
            _tenant.Contexto(scope, c.UnidadeId);
            await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new RevogarVinculoEstacaoCommand(c.Painel));
        }

        var depois = await InvocarFiltroAsync(principal);
        depois.Erro.ShouldBeOfType<HubException>().Message.ShouldBe(PafHubFilter.MensagemRecusada);
        depois.Abortada.ShouldBeTrue("o filtro aborta a conexão da credencial revogada");
    }

    [Fact]
    public async Task Filtro_ChamadaComSessaoRevogada_AbortaERecusa()
    {
        var c = await _op.CriarAsync();
        var usuario = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        var token = (await _tenant.EntrarAsync(usuario)).AccessToken;
        var principal = Principal(token);

        (await InvocarFiltroAsync(principal)).Abortada.ShouldBeFalse("sessão ativa passa pelo filtro");

        using (var scope = factory.Services.CreateScope())
        {
            _tenant.Contexto(scope, unidadeCorrente: null);
            (await scope.ServiceProvider.GetRequiredService<ISessionService>()
                .RevokeSessionForAdminAsync(PafTenantCliente.SessaoDoToken(token), "teste", "teste")).ShouldBeTrue();
        }

        var depois = await InvocarFiltroAsync(principal);
        depois.Erro.ShouldBeOfType<HubException>();
        depois.Abortada.ShouldBeTrue("o filtro aborta a conexão da sessão revogada");
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private static Task Fechamento(HubConnection conexao)
    {
        var fechou = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conexao.Closed += _ =>
        {
            fechou.TrySetResult();
            return Task.CompletedTask;
        };
        return fechou.Task;
    }

    private static string Token(HttpClient cliente) => cliente.DefaultRequestHeaders.Authorization!.Parameter!;

    private static async Task SelecionarAsync(HttpClient atendente, Guid guiche)
    {
        using var selecao = await atendente.PostAsJsonAsync("/api/v1/paf/sessao/guiche", new { estacaoId = guiche });
        selecao.StatusCode.ShouldBe(HttpStatusCode.NoContent, await selecao.Content.ReadAsStringAsync());
    }

    // The principal the JwtBearer handler would build: the token's claims, unvalidated, under an authenticated identity.
    private static ClaimsPrincipal Principal(string token) =>
        new(new ClaimsIdentity(
            new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token).Claims,
            authenticationType: "Bearer"));

    // A fresh scope with no tenant installed, like a hub call on a detached long-polling connection.
    private async Task<(bool Abortada, Exception? Erro)> InvocarFiltroAsync(ClaimsPrincipal principal)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var servicos = scope.ServiceProvider;
        var contexto = new ContextoDeTeste(principal);
        using var hub = new PafHub(servicos.GetRequiredService<ConexoesPaf>(), servicos, NullLogger<PafHub>.Instance);
        var invocacao = new HubInvocationContext(contexto, servicos, hub, typeof(PafHub).GetMethod(nameof(PafHub.EntrarNoGuiche))!, []);
        var filtro = servicos.GetRequiredService<PafHubFilter>();
        try
        {
            await filtro.InvokeMethodAsync(invocacao, _ => ValueTask.FromResult<object?>(null));
            return (contexto.Abortada, null);
        }
        catch (HubException ex)
        {
            return (contexto.Abortada, ex);
        }
    }

    private async Task<HubConnection> ConectarAsync(string token)
    {
        var conexao = await _op.ConectarAsync(token);
        _conexoes.Add(conexao);
        return conexao;
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
