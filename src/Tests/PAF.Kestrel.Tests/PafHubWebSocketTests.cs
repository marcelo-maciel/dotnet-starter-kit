using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace PAF.Kestrel.Tests;

/// <summary>
/// O16: the PafHub over a real WebSocket on Kestrel, with the front's client configuration (WebSockets only,
/// SkipNegotiation, token and tenant in the query). Integration.Tests covers the same rules on long polling, except
/// the SessaoInvalidada notice of a revocation that follows the connect, which long polling cannot deliver reliably.
/// </summary>
[Collection(PafKestrelColecao.Nome)]
public sealed class PafHubWebSocketTests(PafKestrelFactory factory) : IAsyncLifetime
{
    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(10);

    // The server closes at the token's exp (CloseOnAuthenticationExpiration, checked by a once-a-second heartbeat).
    private static readonly TimeSpan FolgaExpiracao = TimeSpan.FromSeconds(5);

    // A WebSocket gets no request after the upgrade: absence of a server-side close is watched this long.
    private static readonly TimeSpan SilencioEsperado = TimeSpan.FromSeconds(1.5);

    private readonly TenantPaf _tenant = new(factory);
    private readonly List<ConexaoWebSocket> _conexoes = [];

    public Task InitializeAsync() => _tenant.InicializarAsync();

    public async Task DisposeAsync()
    {
        foreach (var conexao in _conexoes)
        {
            await conexao.DisposeAsync();
        }

        _tenant.Admin?.Dispose();
    }

    [Fact]
    public async Task Painel_SobreWebSocket_RecebeSenhaChamadaDoGuiche()
    {
        var c = await _tenant.CriarUnidadeAsync();
        var painel = await _tenant.CredencialAsync(c.UnidadeId, c.Painel, PafPapeisEstacao.Painel);
        var conexao = Conexao(_tenant.TenantId, () => Task.FromResult(painel.AccessToken));
        var recebida = Receber<SenhaChamada>(conexao, PafHubMetodos.SenhaChamada);
        await conexao.Hub.StartAsync();
        await conexao.ProntaAsync();

        // SkipNegotiation leaves no fallback: Connected plus a 101 on the only attempt is the WebSocket transport.
        conexao.Hub.State.ShouldBe(HubConnectionState.Connected);
        conexao.Tentativas.Select(t => t.Status).ShouldBe([HttpStatusCode.SwitchingProtocols]);

        var totem = await _tenant.CredencialAsync(c.UnidadeId, c.Totem, PafPapeisEstacao.Emissao);
        using var clienteTotem = _tenant.Cliente(totem.AccessToken);
        var ficha = await EmitirAsync(clienteTotem, c);
        await _tenant.EnvelhecerFichasAsync(c.UnidadeId);

        using var atendente = _tenant.Cliente(await _tenant.AtendenteAsync(c.UnidadeId));
        using (var selecao = await atendente.PostAsJsonAsync("/api/v1/paf/sessao/guiche", new { estacaoId = c.Guiche }))
        {
            selecao.StatusCode.ShouldBe(HttpStatusCode.NoContent, await selecao.Content.ReadAsStringAsync());
        }

        using var resposta = await atendente.PostAsync("/api/v1/paf/guiche/chamar-proximo", content: null);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        var chamada = (await resposta.Content.ReadFromJsonAsync<ChamadaDto>())!;

        var aviso = await recebida.WaitAsync(Espera);
        aviso.AtendimentoId.ShouldBe(chamada.AtendimentoId);
        aviso.FichaId.ShouldBe(ficha.FichaId);
        aviso.Senha.ShouldBe(chamada.Senha);
        aviso.EstacaoId.ShouldBe(c.Guiche);
    }

    [Fact]
    public async Task TenantDeOutroClienteNaQuery_UpgradeRecusadoCom403()
    {
        var c = await _tenant.CriarUnidadeAsync();
        var painel = await _tenant.CredencialAsync(c.UnidadeId, c.Painel, PafPapeisEstacao.Painel);
        var outro = new TenantPaf(factory);
        await outro.InicializarAsync();
        outro.Admin.Dispose();

        var recusada = Conexao(outro.TenantId, () => Task.FromResult(painel.AccessToken));
        await Should.ThrowAsync<Exception>(() => recusada.Hub.StartAsync());
        recusada.Hub.State.ShouldBe(HubConnectionState.Disconnected);
        recusada.Tentativas.Select(t => t.Status).ShouldBe([HttpStatusCode.Forbidden]);

        // Same token, its own tenant: the refusal above is the tenant, not the credential.
        var aceita = Conexao(_tenant.TenantId, () => Task.FromResult(painel.AccessToken));
        await aceita.Hub.StartAsync();
        await aceita.ProntaAsync();
        aceita.Tentativas.Select(t => t.Status).ShouldBe([HttpStatusCode.SwitchingProtocols]);
    }

    // Owner's decision: production takes WebSockets only (OpcoesTempoReal default; this host overrides nothing). Long
    // polling re-authenticates every poll, and a revocation's notice can be lost on a refused poll.
    [Fact]
    public async Task PadraoDeProducao_LongPollingRecusado_WebSocketAceito()
    {
        var c = await _tenant.CriarUnidadeAsync();
        var painel = await _tenant.CredencialAsync(c.UnidadeId, c.Painel, PafPapeisEstacao.Painel);

        using (var cliente = _tenant.Cliente(painel.AccessToken))
        using (var negociacao = await cliente.PostAsync($"{PafHubRota.Caminho}/negotiate?negotiateVersion=1", content: null))
        {
            negociacao.StatusCode.ShouldBe(HttpStatusCode.OK, await negociacao.Content.ReadAsStringAsync());
            using var corpo = JsonDocument.Parse(await negociacao.Content.ReadAsStringAsync());
            corpo.RootElement.GetProperty("availableTransports").EnumerateArray()
                .Select(t => t.GetProperty("transport").GetString()).ShouldBe([nameof(HttpTransportType.WebSockets)]);
        }

        await using var longPolling = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Endereco, $"{PafHubRota.Caminho}?tenant={Uri.EscapeDataString(_tenant.TenantId)}"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(painel.AccessToken);
            })
            .Build();
        await Should.ThrowAsync<Exception>(() => longPolling.StartAsync());
        longPolling.State.ShouldBe(HubConnectionState.Disconnected);

        // Same credential on the front's transport: the refusal above is the transport, not the credential.
        var webSocket = Conexao(_tenant.TenantId, () => Task.FromResult(painel.AccessToken));
        await webSocket.Hub.StartAsync();
        await webSocket.ProntaAsync();
        webSocket.Tentativas.Select(t => t.Status).ShouldBe([HttpStatusCode.SwitchingProtocols]);
    }

    [Fact]
    public async Task TokenDeUmMinuto_ServidorFechaNaExpiracao_EReconectaComTokenRenovado()
    {
        var c = await _tenant.CriarUnidadeAsync();
        var painel = await _tenant.CredencialAsync(c.UnidadeId, c.Painel, PafPapeisEstacao.Painel);
        var expiracao = new JwtSecurityTokenHandler().ReadJwtToken(painel.AccessToken).ValidTo;
        (expiracao - DateTime.UtcNow).ShouldBeLessThanOrEqualTo(PafKestrelFactory.ValidadeToken, "credencial de um minuto");

        // Like the painel: first attempt with the redeemed token, every later one renews through the secret.
        var renovados = new List<string>();
        var primeira = true;
        var conexao = Conexao(_tenant.TenantId, async () =>
        {
            if (primeira)
            {
                primeira = false;
                return painel.AccessToken;
            }

            var novo = (await _tenant.RenovarCredencialAsync(painel.Segredo)).AccessToken;
            renovados.Add(novo);
            return novo;
        });
        var queda = conexao.Queda();
        var reconexao = conexao.Reconexao();
        await conexao.Hub.StartAsync();
        await conexao.ProntaAsync();

        var caiuEm = await queda.WaitAsync(PafKestrelFactory.ValidadeToken + FolgaExpiracao);
        caiuEm.UtcDateTime.ShouldBeGreaterThanOrEqualTo(expiracao, "o servidor não fecha antes do exp");
        caiuEm.UtcDateTime.ShouldBeLessThan(expiracao + FolgaExpiracao, "o servidor fecha no exp, não por outro motivo depois");

        await reconexao.WaitAsync(Espera);
        await conexao.ProntaAsync();
        conexao.Hub.State.ShouldBe(HubConnectionState.Connected);
        var tentativas = conexao.Tentativas;
        tentativas.Select(t => t.Status).ShouldBe([HttpStatusCode.SwitchingProtocols, HttpStatusCode.SwitchingProtocols]);
        tentativas[0].Token.ShouldBe(painel.AccessToken);
        tentativas[1].Token.ShouldBe(renovados.ShouldHaveSingleItem());
        tentativas[1].Token.ShouldNotBe(painel.AccessToken);

        // The expired token still passes the JWT check inside the kit's clock skew, but the server closes it at once.
        var vencida = Conexao(_tenant.TenantId, () => Task.FromResult(painel.AccessToken));
        var quedaVencida = vencida.Queda();
        // Inside the kit's 2-min ClockSkew the upgrade is accepted; a refusal here means the skew changed.
        await vencida.Hub.StartAsync();
        await quedaVencida.WaitAsync(Espera);
        vencida.Hub.State.ShouldNotBe(HubConnectionState.Connected);
    }

    [Fact]
    public async Task SessaoRevogada_ConexaoAbertaSobrevive_EAbortaNaProximaChamada()
    {
        var c = await _tenant.CriarUnidadeAsync();
        var token = await _tenant.AtendenteAsync(c.UnidadeId);
        var conexao = Conexao(_tenant.TenantId, () => Task.FromResult(token));
        var queda = conexao.Queda();
        var fechamento = conexao.Fechamento();
        await conexao.Hub.StartAsync();
        await conexao.ProntaAsync();

        using (var scope = factory.Services.CreateScope())
        {
            _tenant.DefinirTenant(scope);
            (await scope.ServiceProvider.GetRequiredService<ISessionService>()
                .RevokeSessionForAdminAsync(TenantPaf.SessaoDoToken(token), "teste", "teste")).ShouldBeTrue();
        }

        // After the upgrade nothing re-checks the session until the client calls: the connection is still up.
        await Task.Delay(SilencioEsperado);
        queda.IsCompleted.ShouldBeFalse("sem chamada, nada no servidor revalida a sessão");
        conexao.Hub.State.ShouldBe(HubConnectionState.Connected);

        var erro = await Record.ExceptionAsync(() => conexao.Hub.InvokeAsync<Guid?>(PafHubChamadas.EntrarNoGuiche));
        erro.ShouldNotBeNull("a chamada com a sessão revogada é recusada");
        if (erro is HubException recusa)
        {
            recusa.Message.ShouldBe(PafHubFilter.MensagemRecusada);
        }

        // The filter's Abort closes for good (no automatic reconnect), so the front's onclose loop starts over.
        await queda.WaitAsync(Espera);
        await fechamento.WaitAsync(Espera);
        conexao.Hub.State.ShouldBe(HubConnectionState.Disconnected);
        conexao.Tentativas.Select(t => t.Status).ShouldBe([HttpStatusCode.SwitchingProtocols]);

        // Starting over with the same token: the revoked session is refused at the upgrade.
        var nova = Conexao(_tenant.TenantId, () => Task.FromResult(token));
        await Should.ThrowAsync<Exception>(() => nova.Hub.StartAsync());
        nova.Tentativas.Select(t => t.Status).ShouldBe([HttpStatusCode.Unauthorized]);
    }

    // Seção 7, "Revogar sessão encerra a conexão" (RF22): the PAF-born revocation sends SessaoInvalidada with the
    // screen's sentence to the session's connections before closing them. On WebSockets, the front's transport: on
    // long polling every poll is a new request through the session check, so once the revocation commits a poll not
    // already parked is refused with 401 and the notice queued for it never leaves (Integration.Tests cannot hold this).
    [Fact]
    public async Task RevogacaoNoPaf_UnidadeRetirada_AvisaComOMotivoDaTelaAntesDeFechar()
    {
        var c = await _tenant.CriarUnidadeAsync();
        var token = await _tenant.AtendenteAsync(c.UnidadeId);
        var sessaoId = TenantPaf.SessaoDoToken(token);
        var conexao = Conexao(_tenant.TenantId, () => Task.FromResult(token));
        var avisos = Avisos(conexao);
        var fechamento = conexao.Fechamento();
        await conexao.Hub.StartAsync();
        await conexao.ProntaAsync();

        using (var scope = factory.Services.CreateScope())
        {
            _tenant.DefinirTenant(scope);
            var sessao = (await scope.ServiceProvider.GetRequiredService<ISessionService>().GetSessionAsync(sessaoId)).ShouldNotBeNull();
            await scope.ServiceProvider.GetRequiredService<RevogacaoSessao>().RevogarAsync(
                _tenant.TenantId, sessaoId, Guid.Parse(sessao.UserId!), RevogacaoSessao.MotivoUnidadeRetirada, CancellationToken.None);
        }

        await fechamento.WaitAsync(Espera);
        var aviso = await AvisoAntesDoFechamentoAsync(avisos);
        aviso.SessaoId.ShouldBe(sessaoId);
        aviso.Motivo.ShouldBe(RevogacaoSessao.MensagemUnidadeRetirada, "a tela recebe a frase, não o motivo de auditoria do kit");
    }

    // The kit revocation whose event is lost (no outbox dispatcher in this host) is caught by the renewal cycle,
    // which sends the notice before closing; inside the renewal interval the cycle does not look (seção 9).
    [Fact]
    public async Task EventoPerdido_ARevogacaoDoKitValeNoCicloSeguinteDeRenovacao()
    {
        var c = await _tenant.CriarUnidadeAsync();
        var token = await _tenant.AtendenteAsync(c.UnidadeId);
        var sessaoId = TenantPaf.SessaoDoToken(token);
        var conexao = Conexao(_tenant.TenantId, () => Task.FromResult(token));
        var avisos = Avisos(conexao);
        var fechamento = conexao.Fechamento();
        await conexao.Hub.StartAsync();
        await conexao.ProntaAsync();

        using (var scope = factory.Services.CreateScope())
        {
            _tenant.DefinirTenant(scope);
            var sessoes = scope.ServiceProvider.GetRequiredService<ISessionService>();
            var sessao = (await sessoes.GetSessionAsync(sessaoId)).ShouldNotBeNull();
            (await sessoes.RevokeSessionForAdminAsync(sessaoId, sessao.UserId!, "teste: evento perdido")).ShouldBeTrue();
        }

        var renovacao = factory.Services.GetRequiredService<RenovacaoPelaConexao>();
        await renovacao.CicloAsync(CancellationToken.None);
        avisos.Itens.ShouldBeEmpty("dentro do intervalo de renovação o ciclo ainda não olha a sessão");

        using (var scope = factory.Services.CreateScope())
        {
            (await _tenant.Contexto(scope, unidadeCorrente: null).Database.ExecuteSqlAsync($"""
                UPDATE paf."AtividadesSessao" SET "UltimaAtividadeEm" = now() - interval '2 minutes' WHERE "SessaoId" = {sessaoId}
                """)).ShouldBe(1);
        }

        await renovacao.CicloAsync(CancellationToken.None);

        await fechamento.WaitAsync(Espera);
        var aviso = await AvisoAntesDoFechamentoAsync(avisos);
        aviso.SessaoId.ShouldBe(sessaoId);
        aviso.Motivo.ShouldBe(SessaoRevogadaPafHandler.MensagemSessaoEncerrada);
    }

    private static AvisosRecebidos Avisos(ConexaoWebSocket conexao)
    {
        var avisos = new AvisosRecebidos(new ConcurrentQueue<SessaoInvalidada>(), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        conexao.Hub.On<SessaoInvalidada>(SessaoInvalidada.Metodo, aviso =>
        {
            avisos.Itens.Enqueue(aviso);
            avisos.Primeiro.TrySetResult();
        });
        return avisos;
    }

    // The .NET client runs .On handlers on their own loop and raises Closed without waiting for it (HubConnection
    // receive loop, aspnetcore release/10.0), so a notice read before the close can reach the handler after Closed.
    // Nothing is read after the close: a notice delivered at all came before it.
    private static async Task<SessaoInvalidada> AvisoAntesDoFechamentoAsync(AvisosRecebidos avisos)
    {
        (await Task.WhenAny(avisos.Primeiro.Task, Task.Delay(Espera))).ShouldBe(avisos.Primeiro.Task, "o aviso sai antes do fechamento");
        return avisos.Itens.ToArray().ShouldHaveSingleItem("o aviso sai uma vez");
    }

    private sealed record AvisosRecebidos(ConcurrentQueue<SessaoInvalidada> Itens, TaskCompletionSource Primeiro);

    private ConexaoWebSocket Conexao(string tenant, Func<Task<string>> obterToken)
    {
        var conexao = new ConexaoWebSocket(factory.Endereco, tenant, obterToken);
        _conexoes.Add(conexao);
        return conexao;
    }

    private static Task<T> Receber<T>(ConexaoWebSocket conexao, string metodo)
    {
        var recebido = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        conexao.Hub.On<T>(metodo, payload => recebido.TrySetResult(payload));
        return recebido.Task;
    }

    private static async Task<FichaEmitidaDto> EmitirAsync(HttpClient totem, CenarioUnidade c)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/paf/estacao/emissao/fichas");
        request.Content = JsonContent.Create(new EmitirFichaRequest(c.FilaId, c.PrioridadeId));
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var resposta = await totem.SendAsync(request);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<FichaEmitidaDto>())!;
    }
}
