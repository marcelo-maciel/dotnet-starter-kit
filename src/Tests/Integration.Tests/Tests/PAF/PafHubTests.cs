using System.Collections.Concurrent;
using System.Security.Claims;
using FSH.Framework.Shared.Constants;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.TempoReal;
using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// PafHub (seção 7): query-string token on its route, policy at negotiate, server-decided groups keyed
/// by tenant, and both delivery paths of seção 8 item 6 (direct send and outbox re-send).
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafHubTests : IAsyncLifetime
{
    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(5);

    // Absence is asserted after a positive delivery on the same server, plus this margin.
    private static readonly TimeSpan SilencioEsperado = TimeSpan.FromSeconds(1.5);

    private readonly FshWebApplicationFactory _factory;
    private readonly PafBanco _banco;
    private readonly List<HubConnection> _conexoes = [];

    public PafHubTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _banco = new PafBanco(factory);
    }

    public Task InitializeAsync() => _banco.InicializarAsync();

    public async Task DisposeAsync()
    {
        foreach (var conexao in _conexoes)
        {
            await conexao.DisposeAsync();
        }
    }

    [Fact]
    public async Task Negociar_SemToken_Recusa401()
    {
        using var client = _factory.CreateClient();
        using var resposta = await client.PostAsync($"{PafHubRota.Caminho}/negotiate?negotiateVersion=1", content: null);
        resposta.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Negociar_TokenDeEstacaoNaQuery_Aceita()
    {
        var token = await TokenEstacaoAsync(TestConstants.RootTenantId, PafPapeisEstacao.Painel, Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(5));
        using var client = _factory.CreateClient();
        using var resposta = await client.PostAsync(
            $"{PafHubRota.Caminho}/negotiate?negotiateVersion=1&tenant={TestConstants.RootTenantId}&access_token={token}", content: null);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TokenNaQuery_ForaDaListaDoKit_ContinuaRecusado()
    {
        // The kit edit must stay as narrow as ADR 0008 says: the query token only counts on the hub routes.
        var token = await TokenEstacaoAsync(TestConstants.RootTenantId, PafPapeisEstacao.Painel, Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(5));
        using var client = _factory.CreateClient();
        using var resposta = await client.GetAsync($"/api/v1/realtime/presence?userIds=x&access_token={token}");
        resposta.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("Guiche", true, true)]
    [InlineData("", true, true)]
    [InlineData(PafPapeisEstacao.Painel, false, true)]
    [InlineData(PafPapeisEstacao.Emissao, true, false)]
    public async Task Negociar_CredencialDeEstacaoMalFormada_Recusa403(string papel, bool comEstacao, bool comUnidade)
    {
        var claims = new List<Claim> { new(ClaimConstants.Tenant, TestConstants.RootTenantId), new(PafClaims.Papel, papel) };
        if (comEstacao)
        {
            claims.Add(new Claim(PafClaims.Estacao, Guid.NewGuid().ToString()));
        }

        if (comUnidade)
        {
            claims.Add(new Claim(PafClaims.Unidade, Guid.NewGuid().ToString()));
        }

        var token = await EmitirAsync(claims, TimeSpan.FromMinutes(5));
        using var client = _factory.CreateClient();
        using var resposta = await client.PostAsync(
            $"{PafHubRota.Caminho}/negotiate?negotiateVersion=1&access_token={token}", content: null);
        resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Conectar_ComTokenSoNaQuery_FicaConectado()
    {
        var token = await TokenEstacaoAsync(TestConstants.RootTenantId, PafPapeisEstacao.Painel, Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(5));

        var conexao = await ConectarAsync(token, TestConstants.RootTenantId);

        conexao.State.ShouldBe(HubConnectionState.Connected);
    }

    [Fact]
    public async Task EnvioDireto_ChegaSoAoGrupoCerto_NuncaAOutraUnidadeOuOutroTenant()
    {
        var outroTenant = await CriarOutroTenantAsync();
        var unidadeA = Guid.NewGuid();
        var unidadeB = Guid.NewGuid();
        var painelA = Guid.NewGuid();
        var guicheA = Guid.NewGuid();

        var painelDaUnidadeA = await ConectarEstacaoAsync(TestConstants.RootTenantId, PafPapeisEstacao.Painel, painelA, unidadeA);
        var totemDaUnidadeA = await ConectarEstacaoAsync(TestConstants.RootTenantId, PafPapeisEstacao.Emissao, Guid.NewGuid(), unidadeA);
        var painelDaUnidadeB = await ConectarEstacaoAsync(TestConstants.RootTenantId, PafPapeisEstacao.Painel, Guid.NewGuid(), unidadeB);
        // Same unit id AND same station id, other tenant: only the tenant in the group key keeps them apart.
        var gemeoNoOutroTenant = await ConectarEstacaoAsync(outroTenant, PafPapeisEstacao.Painel, painelA, unidadeA);

        using var chamadaNoPainelA = new Caixa<SenhaChamada>(painelDaUnidadeA, PafHubMetodos.SenhaChamada);
        using var chamadaNoTotemA = new Caixa<SenhaChamada>(totemDaUnidadeA, PafHubMetodos.SenhaChamada);
        using var chamadaNoPainelB = new Caixa<SenhaChamada>(painelDaUnidadeB, PafHubMetodos.SenhaChamada);
        using var chamadaNoGemeo = new Caixa<SenhaChamada>(gemeoNoOutroTenant, PafHubMetodos.SenhaChamada);
        using var filaNoPainelA = new Caixa<FilaAtualizada>(painelDaUnidadeA, PafHubMetodos.FilaAtualizada);
        using var filaNoTotemA = new Caixa<FilaAtualizada>(totemDaUnidadeA, PafHubMetodos.FilaAtualizada);
        using var filaNoPainelB = new Caixa<FilaAtualizada>(painelDaUnidadeB, PafHubMetodos.FilaAtualizada);
        using var filaNoGemeo = new Caixa<FilaAtualizada>(gemeoNoOutroTenant, PafHubMetodos.FilaAtualizada);

        var chamada = new SenhaChamada(Guid.NewGuid(), unidadeA, Guid.NewGuid(), Guid.NewGuid(), 1, "A001", guicheA, "Guichê 1");
        var fila = new FilaAtualizada(Guid.NewGuid(), unidadeA, Guid.NewGuid());
        using (var scope = _factory.Services.CreateScope())
        {
            var avisos = scope.ServiceProvider.GetRequiredService<AvisosTempoReal>();
            await avisos.EnviarAsync(SenhaChamadaPaf.Criar(TestConstants.RootTenantId, "teste", chamada, [painelA]), CancellationToken.None);
            await avisos.EnviarAsync(FilaAtualizadaPaf.Criar(TestConstants.RootTenantId, "teste", fila), CancellationToken.None);
        }

        var recebida = await chamadaNoPainelA.EsperarAsync(c => c.EventoId == chamada.EventoId, Espera);
        recebida.ShouldNotBeNull("o painel dirigido da unidade A recebe a chamada");
        recebida.Versao.ShouldBe(1);
        recebida.Senha.ShouldBe("A001");
        (await filaNoPainelA.EsperarAsync(f => f.EventoId == fila.EventoId, Espera)).ShouldNotBeNull();
        (await filaNoTotemA.EsperarAsync(f => f.EventoId == fila.EventoId, Espera)).ShouldNotBeNull("toda conexão da unidade recebe a mudança de fila");

        await Task.Delay(SilencioEsperado);
        chamadaNoTotemA.Recebidos.ShouldBeEmpty("o totem não está no grupo do painel");
        chamadaNoPainelB.Recebidos.ShouldBeEmpty("outra unidade não recebe a chamada");
        filaNoPainelB.Recebidos.ShouldBeEmpty("outra unidade não recebe a mudança de fila");
        chamadaNoGemeo.Recebidos.ShouldBeEmpty("outro tenant com os mesmos ids não recebe a chamada");
        filaNoGemeo.Recebidos.ShouldBeEmpty("outro tenant com os mesmos ids não recebe a mudança de fila");
    }

    [Fact]
    public async Task CaixaDeSaida_ReenviaComOMesmoId_SoSeOAtendimentoAindaEstaNoEstadoAnunciado()
    {
        var c = await _banco.CriarCadastroAsync();
        var (fichaChamada, chamado) = await _banco.EmitirAsync(c, 1);
        var (fichaEncerrada, encerrado) = await _banco.EmitirAsync(c, 2);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = _banco.Contexto(scope, c.UnidadeId);
            (await PafBanco.ChamarAsync(db, chamado.Id, c.GuicheA)).ShouldBe(1);
            (await PafBanco.ChamarAsync(db, encerrado.Id, c.GuicheB)).ShouldBe(1);
            (await PafBanco.EncerrarAsync(db, encerrado.Id)).ShouldBe(1);
        }

        var painel = Guid.NewGuid();
        var conexao = await ConectarEstacaoAsync(TestConstants.RootTenantId, PafPapeisEstacao.Painel, painel, c.UnidadeId);
        using var chamadas = new Caixa<SenhaChamada>(conexao, PafHubMetodos.SenhaChamada);
        using var atualizacoes = new Caixa<AtendimentoAtualizado>(conexao, PafHubMetodos.AtendimentoAtualizado);

        var aindaChamado = new SenhaChamada(Guid.NewGuid(), c.UnidadeId, chamado.Id, fichaChamada.Id, 1, "A001", c.GuicheA, "Guichê 1");
        // A late copy of a call whose atendimento was closed in the meantime: must never be announced.
        var jaEncerrado = new SenhaChamada(Guid.NewGuid(), c.UnidadeId, encerrado.Id, fichaEncerrada.Id, 1, "A002", c.GuicheB, "Guichê 2");
        var encerramento = new AtendimentoAtualizado(Guid.NewGuid(), c.UnidadeId, encerrado.Id, fichaEncerrada.Id, 2, "Encerrado", c.GuicheB);
        var estadoInvalido = new AtendimentoAtualizado(Guid.NewGuid(), c.UnidadeId, chamado.Id, fichaChamada.Id, 1, "Inexistente", c.GuicheA);

        using (var scope = _factory.Services.CreateScope())
        {
            _banco.Contexto(scope, unidadeCorrente: null);
            var avisos = scope.ServiceProvider.GetRequiredService<AvisosTempoReal>();
            await avisos.GravarAsync(SenhaChamadaPaf.Criar(TestConstants.RootTenantId, "teste", aindaChamado, [painel]), CancellationToken.None);
            await avisos.GravarAsync(SenhaChamadaPaf.Criar(TestConstants.RootTenantId, "teste", jaEncerrado, [painel]), CancellationToken.None);
            await avisos.GravarAsync(AtendimentoAtualizadoPaf.Criar(TestConstants.RootTenantId, "teste", encerramento), CancellationToken.None);
            await avisos.GravarAsync(AtendimentoAtualizadoPaf.Criar(TestConstants.RootTenantId, "teste", estadoInvalido), CancellationToken.None);
        }

        await OutboxDrain.DrainAsync(_factory.Services);

        var reenviada = await chamadas.EsperarAsync(s => s.EventoId == aindaChamado.EventoId, Espera);
        reenviada.ShouldNotBeNull("o atendimento continua Chamado, então a caixa de saída reenvia");
        reenviada.AtendimentoId.ShouldBe(chamado.Id);
        (await atualizacoes.EsperarAsync(a => a.EventoId == encerramento.EventoId, Espera))
            .ShouldNotBeNull("o atendimento está Encerrado, como o aviso anuncia");

        await Task.Delay(SilencioEsperado);
        chamadas.Recebidos.ShouldNotContain(s => s.EventoId == jaEncerrado.EventoId, "chamada atrasada de atendimento encerrado não é anunciada");
        atualizacoes.Recebidos.ShouldNotContain(a => a.EventoId == estadoInvalido.EventoId);

        await using var verificacao = _factory.Services.CreateAsyncScope();
        var eventing = verificacao.ServiceProvider.GetRequiredService<FSH.Framework.Eventing.Persistence.EventingDbContext>();
        var ids = new[] { aindaChamado.EventoId, jaEncerrado.EventoId, encerramento.EventoId, estadoInvalido.EventoId };
        var pendentes = await eventing.OutboxMessages.CountAsync(m => ids.Contains(m.Id) && (m.ProcessedOnUtc == null || m.IsDead));
        pendentes.ShouldBe(0, "o descarte é decisão, não falha: nenhuma linha fica para nova tentativa");
    }

    [Fact]
    public async Task Conexao_CaiQuandoOTokenVence()
    {
        var curto = await TokenEstacaoAsync(TestConstants.RootTenantId, PafPapeisEstacao.Painel, Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromSeconds(4));
        var longo = await TokenEstacaoAsync(TestConstants.RootTenantId, PafPapeisEstacao.Painel, Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(5));
        var vence = await ConectarAsync(curto, TestConstants.RootTenantId);
        var controle = await ConectarAsync(longo, TestConstants.RootTenantId);
        var fechou = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vence.Closed += _ =>
        {
            fechou.TrySetResult();
            return Task.CompletedTask;
        };

        var primeiro = await Task.WhenAny(fechou.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        primeiro.ShouldBe(fechou.Task, "CloseOnAuthenticationExpiration encerra a conexão quando o token vence");
        // Control: the same transport with a valid token stays up, so the close above is the expiry.
        controle.State.ShouldBe(HubConnectionState.Connected);
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private Task<string> TokenEstacaoAsync(string tenantId, string papel, Guid estacaoId, Guid unidadeId, TimeSpan validade) =>
        EmitirAsync(
            [
                new Claim(ClaimConstants.Tenant, tenantId),
                new Claim(PafClaims.Papel, papel),
                new Claim(PafClaims.Estacao, estacaoId.ToString()),
                new Claim(PafClaims.Unidade, unidadeId.ToString()),
            ],
            validade);

    private async Task<string> EmitirAsync(IReadOnlyList<Claim> claims, TimeSpan validade)
    {
        using var scope = _factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();
        var (accessToken, _) = await tokens.IssueAccessOnlyAsync("paf-hub-teste", claims, validade, CancellationToken.None);
        return accessToken;
    }

    private async Task<HubConnection> ConectarEstacaoAsync(string tenantId, string papel, Guid estacaoId, Guid unidadeId)
    {
        var token = await TokenEstacaoAsync(tenantId, papel, estacaoId, unidadeId, TimeSpan.FromMinutes(5));
        return await ConectarAsync(token, tenantId);
    }

    private async Task<HubConnection> ConectarAsync(string token, string tenantId)
    {
        var conexao = new HubConnectionBuilder()
            .WithUrl(
                $"http://localhost{PafHubRota.Caminho}?tenant={Uri.EscapeDataString(tenantId)}&access_token={Uri.EscapeDataString(token)}",
                options =>
                {
                    // Long polling: a WebSocket through TestServer.CreateWebSocketClient is accepted and then
                    // closed before the SignalR handshake, on the kit's AppHub too (probed 17/10/2026), so
                    // WebSocket is not exercised here. No AccessTokenProvider: the token only travels in the URL.
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                })
            .Build();
        _conexoes.Add(conexao);
        await conexao.StartAsync();
        return conexao;
    }

    private async Task<string> CriarOutroTenantAsync()
    {
        var auth = new AuthHelper(_factory);
        using var root = await auth.CreateRootAdminClientAsync();
        var id = $"paf-hub-{Guid.NewGuid():N}"[..20];
        using var resposta = await root.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id,
            name = $"Tenant {id}",
            connectionString = (string?)null,
            adminEmail = $"admin-{id}@tenant.com",
            adminPassword = TestConstants.DefaultPassword,
            issuer = $"{id}.issuer",
        });
        resposta.StatusCode.ShouldBe(HttpStatusCode.Created, await resposta.Content.ReadAsStringAsync());
        await TenantProvisioningWait.WaitForProvisioningAsync(root, id);
        return id;
    }

    private sealed class Caixa<T> : IDisposable
    {
        private readonly ConcurrentQueue<T> _itens = new();
        private readonly SemaphoreSlim _sinal = new(0);
        private readonly IDisposable _inscricao;

        public Caixa(HubConnection conexao, string metodo)
        {
            _inscricao = conexao.On<T>(metodo, item =>
            {
                _itens.Enqueue(item);
                _sinal.Release();
            });
        }

        public IReadOnlyList<T> Recebidos => [.. _itens];

        public async Task<T?> EsperarAsync(Func<T, bool> criterio, TimeSpan limite)
        {
            var prazo = DateTime.UtcNow + limite;
            while (true)
            {
                var achado = _itens.FirstOrDefault(criterio);
                if (achado is not null)
                {
                    return achado;
                }

                var resta = prazo - DateTime.UtcNow;
                if (resta <= TimeSpan.Zero || !await _sinal.WaitAsync(resta))
                {
                    return _itens.FirstOrDefault(criterio);
                }
            }
        }

        public void Dispose()
        {
            _inscricao.Dispose();
            _sinal.Dispose();
        }
    }
}
