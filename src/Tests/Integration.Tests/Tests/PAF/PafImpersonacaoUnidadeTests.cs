using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Prioridades;
using FSH.Modules.PAF.Contracts.v1.Sessao;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Sessao.SetUnidadeCorrente;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Sessions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// S10B: a impersonação da Codefi tem unidade corrente própria, chaveada pela concessão (jti), nunca pela sessão do
/// usuário (seção 9, RF22). O host do harness sobe sem Valkey, então as requisições exercitam a ordem pelo
/// PostgreSQL (modo degradado); o caminho com Valkey usa um contêiner real e os serviços reais sobre o banco do harness.
/// A unidade corrente é observada por uma leitura com filtro de unidade (lista de prioridades), sem cabeçalho.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafImpersonacaoUnidadeTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Impersonacao = TestConstants.IdentityBasePath + "/impersonation";
    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SilencioEsperado = TimeSpan.FromSeconds(1.5);

    private readonly PafTenantCliente _tenant = new(factory, "paf-imu");
    private readonly RedisContainer _valkey = new RedisBuilder("valkey/valkey:9.1.0-alpine").Build();
    private readonly List<HubConnection> _conexoes = [];
    private ConnectionMultiplexer _multiplexer = default!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_valkey.StartAsync(), _tenant.InicializarAsync());
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(_valkey.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        foreach (var conexao in _conexoes)
        {
            await conexao.DisposeAsync();
        }

        await _multiplexer.DisposeAsync();
        await _valkey.DisposeAsync();
    }

    // ─── pelas requisições (sem Valkey no host) ───────────────────────

    [Fact]
    public async Task SemSessaoViva_AbreNaPrincipal_IgnoraCabecalho_ENaoGravaAtividade()
    {
        var (a, b, alvo) = await CenarioAsync();
        using var suporte = _tenant.Cliente(await IniciarAsync(alvo.UserId));

        (await NomesAsync(suporte, cabecalho: null)).ShouldBe(["PA"]);
        (await NomesAsync(suporte, cabecalho: b.UnidadeId)).ShouldBe(["PA"], "X-Paf-Unidade não vale para act_sub: nem troca, nem 403");
        (await _tenant.AtividadesAsync(Guid.Parse(alvo.UserId))).ShouldBeEmpty("a impersonação não ocupa licença");
        a.UnidadeId.ShouldNotBe(b.UnidadeId);
    }

    [Fact]
    public async Task ComSessaoVivaDoUsuario_AbreNaUnidadeDela()
    {
        var (_, b, alvo) = await CenarioAsync();
        var token = await _tenant.EntrarAsync(alvo);
        var sessaoId = PafTenantCliente.SessaoDoToken(token.AccessToken);
        // Without Valkey the live session's unit is its licence row; moved to B as if the session had switched there.
        (await _tenant.ExecutarAsync($"""UPDATE paf."AtividadesSessao" SET "UnidadeId" = {b.UnidadeId} WHERE "SessaoId" = {sessaoId}""")).ShouldBe(1);

        using var suporte = _tenant.Cliente(await IniciarAsync(alvo.UserId));

        (await NomesAsync(suporte, cabecalho: null)).ShouldBe(["PB"]);
    }

    [Fact]
    public async Task SemValkey_ComVinculoDaConcessao_OperaNaUnidadeDaEstacao()
    {
        var (_, b, alvo) = await CenarioAsync();
        var token = await IniciarAsync(alvo.UserId);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        await VincularConcessaoAsync(b.GuicheA, Guid.Parse(jwt.Id), new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero));

        using var suporte = _tenant.Cliente(token);

        (await NomesAsync(suporte, cabecalho: null)).ShouldBe(["PB"], "o vínculo da concessão vence a principal do impersonado");
    }

    [Fact]
    public async Task LoginDoUsuarioEmOutraMaquina_NaoDerrubaOSuporte()
    {
        var (_, _, alvo) = await CenarioAsync();
        using var suporte = _tenant.Cliente(await IniciarAsync(alvo.UserId));
        (await NomesAsync(suporte, cabecalho: null)).ShouldBe(["PA"]);

        await _tenant.EntrarAsync(alvo);
        await _tenant.EntrarAsync(alvo);

        (await NomesAsync(suporte, cabecalho: null)).ShouldBe(["PA"], "a sessão única do usuário não alcança a concessão");
        (await _tenant.AtividadesAsync(Guid.Parse(alvo.UserId))).Count.ShouldBe(1, "só a última sessão do usuário ocupa licença");
    }

    [Fact]
    public async Task TrocaDoSuporte_SemValkey_Recusada_ForaDoEscopo_Proibida()
    {
        var (_, b, alvo) = await CenarioAsync();
        var outra = await _tenant.CriarUnidadeAsync(limiteSessoes: null);
        using var suporte = _tenant.Cliente(await IniciarAsync(alvo.UserId));

        using var semValkey = await PafApi.EnviarAsync(suporte, HttpMethod.Post, "/sessao/unidade-corrente", null, new SetUnidadeCorrenteCommand(b.UnidadeId));
        semValkey.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, await semValkey.Content.ReadAsStringAsync());

        using var fora = await PafApi.EnviarAsync(suporte, HttpMethod.Post, "/sessao/unidade-corrente", null, new SetUnidadeCorrenteCommand(outra.UnidadeId));
        fora.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await fora.Content.ReadAsStringAsync());

        (await NomesAsync(suporte, cabecalho: null)).ShouldBe(["PA"]);
    }

    [Fact]
    public async Task Hub_ConexaoDoSuporte_EntraNaUnidadeDaConcessao_NuncaNoGrupoDoUsuario()
    {
        var (a, b, alvo) = await CenarioAsync();
        var usuario = await ConectarAsync((await _tenant.EntrarAsync(alvo)).AccessToken);
        var suporte = await ConectarAsync(await IniciarAsync(alvo.UserId));
        using var suporteRecebe = new Caixa<FilaAtualizada>(suporte, PafHubMetodos.FilaAtualizada);
        using var usuarioRecebe = new Caixa<FilaAtualizada>(usuario, PafHubMetodos.FilaAtualizada);

        var naUnidadeA = new FilaAtualizada(Guid.NewGuid(), a.UnidadeId, Guid.NewGuid());
        var naUnidadeB = new FilaAtualizada(Guid.NewGuid(), b.UnidadeId, Guid.NewGuid());
        var aoUsuario = new FilaAtualizada(Guid.NewGuid(), a.UnidadeId, Guid.NewGuid());
        var hub = factory.Services.GetRequiredService<IHubContext<PafHub>>();
        await hub.Clients.Group(PafGrupos.Unidade(_tenant.TenantId, a.UnidadeId)).SendAsync(PafHubMetodos.FilaAtualizada, naUnidadeA);
        await hub.Clients.Group(PafGrupos.Unidade(_tenant.TenantId, b.UnidadeId)).SendAsync(PafHubMetodos.FilaAtualizada, naUnidadeB);
        await hub.Clients.Group(PafGrupos.Usuario(_tenant.TenantId, alvo.UserId)).SendAsync(PafHubMetodos.FilaAtualizada, aoUsuario);

        (await suporteRecebe.EsperarAsync(f => f.EventoId == naUnidadeA.EventoId, Espera)).ShouldNotBeNull("a conexão do suporte está na unidade da concessão");
        (await usuarioRecebe.EsperarAsync(f => f.EventoId == aoUsuario.EventoId, Espera)).ShouldNotBeNull("o grupo do usuário alcança a conexão dele");
        await Task.Delay(SilencioEsperado);
        suporteRecebe.Recebidos.ShouldNotContain(f => f.EventoId == aoUsuario.EventoId, "o suporte nunca entra no grupo por usuário");
        suporteRecebe.Recebidos.ShouldNotContain(f => f.EventoId == naUnidadeB.EventoId, "o suporte não está em outra unidade");
    }

    // ─── com Valkey (contêiner real, serviços reais) ──────────────────

    [Fact]
    public async Task ComValkey_AbreNaUnidadeTrocadaPelaSessaoVivaDoUsuario_EGuardaNaChaveDaConcessao()
    {
        var (_, b, alvo) = await CenarioAsync();
        var sessaoId = PafTenantCliente.SessaoDoToken((await _tenant.EntrarAsync(alvo)).AccessToken);
        var estado = Estado();
        (await estado.DefinirUnidadeAsync(_tenant.TenantId, sessaoId, b.UnidadeId, CancellationToken.None)).ShouldBeTrue();
        var concessao = Concessao(alvo);

        using var scope = factory.Services.CreateScope();
        var unidade = await Resolvedor(scope, estado).ResolverAsync(concessao, CancellationToken.None);

        unidade.ShouldBe(b.UnidadeId);
        (await estado.LerUnidadeDaConcessaoAsync(_tenant.TenantId, concessao.ConcessaoId, CancellationToken.None)).ShouldBe(b.UnidadeId);
        var ttl = await _multiplexer.GetDatabase().KeyTimeToLiveAsync($"paf:{_tenant.TenantId}:concessao:{concessao.ConcessaoId:N}:unidade");
        ttl.ShouldNotBeNull("a chave da concessão vence com o token");
        ttl.Value.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task ComValkey_ChaveGuardadaForaDoEscopo_RecriadaPelaPrincipal()
    {
        var (a, _, alvo) = await CenarioAsync();
        var outra = await _tenant.CriarUnidadeAsync(limiteSessoes: null);
        var estado = Estado();
        var concessao = Concessao(alvo);
        (await estado.DefinirUnidadeDaConcessaoAsync(_tenant.TenantId, concessao.ConcessaoId, outra.UnidadeId, concessao.ExpiraEm, CancellationToken.None)).ShouldBeTrue();

        using var scope = factory.Services.CreateScope();
        (await Resolvedor(scope, estado).ResolverAsync(concessao, CancellationToken.None)).ShouldBe(a.UnidadeId);
        (await estado.LerUnidadeDaConcessaoAsync(_tenant.TenantId, concessao.ConcessaoId, CancellationToken.None)).ShouldBe(a.UnidadeId);
    }

    [Fact]
    public async Task ComValkey_TrocaDoSuporte_MudaSoAConcessao_SoltaOVinculoDela_ENaoTocaNoUsuario()
    {
        var (a, b, alvo) = await CenarioAsync();
        var sessaoId = PafTenantCliente.SessaoDoToken((await _tenant.EntrarAsync(alvo)).AccessToken);
        var estado = Estado();
        (await estado.DefinirUnidadeAsync(_tenant.TenantId, sessaoId, a.UnidadeId, CancellationToken.None)).ShouldBeTrue();
        var atividadesAntes = await _tenant.AtividadesAsync(Guid.Parse(alvo.UserId));
        var concessao = Concessao(alvo);
        await VincularConcessaoAsync(a.GuicheB, concessao.ConcessaoId, concessao.ExpiraEm);

        using (var scope = factory.Services.CreateScope())
        {
            await HandlerDaConcessao(scope, a.UnidadeId, concessao, estado).Handle(new SetUnidadeCorrenteCommand(b.UnidadeId), CancellationToken.None);
        }

        (await estado.LerUnidadeDaConcessaoAsync(_tenant.TenantId, concessao.ConcessaoId, CancellationToken.None)).ShouldBe(b.UnidadeId);
        (await estado.LerUnidadeAsync(_tenant.TenantId, sessaoId, CancellationToken.None)).ShouldBe(a.UnidadeId, "a troca do suporte não move o usuário");
        var atividadesDepois = await _tenant.AtividadesAsync(Guid.Parse(alvo.UserId));
        atividadesDepois.Select(x => (x.SessaoId, x.UnidadeId)).ShouldBe(atividadesAntes.Select(x => (x.SessaoId, x.UnidadeId)));
        (await VinculosAtivosDaConcessaoAsync(concessao.ConcessaoId)).ShouldBe(0, "a troca solta o guichê da concessão");
    }

    [Fact]
    public async Task ComValkey_TrocaDoSuporteForaDoEscopo_ProibidaSemTocarNaChave()
    {
        var (a, _, alvo) = await CenarioAsync();
        var outra = await _tenant.CriarUnidadeAsync(limiteSessoes: null);
        var estado = Estado();
        var concessao = Concessao(alvo);

        using var scope = factory.Services.CreateScope();
        await Should.ThrowAsync<ForbiddenException>(() => HandlerDaConcessao(scope, a.UnidadeId, concessao, estado)
            .Handle(new SetUnidadeCorrenteCommand(outra.UnidadeId), CancellationToken.None).AsTask());
        (await estado.LerUnidadeDaConcessaoAsync(_tenant.TenantId, concessao.ConcessaoId, CancellationToken.None)).ShouldBeNull();
    }

    // Revisão da onda 3, R2: the switch releases the grant's guichê, and its connections leave that guichê's group with it.
    [Fact]
    public async Task ComValkey_TrocaDoSuporte_TiraAConexaoDaConcessaoDoGrupoDoGuicheSolto()
    {
        var op = new PafOperacao(factory, _tenant);
        try
        {
            var c = await op.CriarAsync();
            var b = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
            var alvo = await _tenant.CriarUsuarioAsync(c.UnidadeId, b.UnidadeId);
            using var totem = await op.TotemAsync(c);
            await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
            await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
            await op.EnvelhecerFichasAsync(c.UnidadeId);
            var token = await IniciarAsync(alvo.UserId);
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
            using var suporte = _tenant.Cliente(token);
            var hub = await ConectarAsync(token);
            using var chamadasDoSuporte = new Caixa<SenhaChamada>(hub, PafHubMetodos.SenhaChamada);
            using (var selecao = await suporte.PostAsJsonAsync("/api/v1/paf/sessao/guiche", new { estacaoId = c.GuicheA, liberarPendencia = false }))
            {
                selecao.StatusCode.ShouldBe(HttpStatusCode.NoContent, await selecao.Content.ReadAsStringAsync());
            }

            var propria = await PafOperacao.ChamarOkAsync(suporte);
            (await chamadasDoSuporte.EsperarAsync(s => s.AtendimentoId == propria.AtendimentoId, Espera))
                .ShouldNotBeNull("antes da troca a conexão do suporte está no grupo do guichê A");
            await PafOperacao.AgirOkAsync(suporte, propria.AtendimentoId, "iniciar");
            await PafOperacao.AgirOkAsync(suporte, propria.AtendimentoId, "encerrar");

            var concessao = new ConcessaoImpersonacao(
                _tenant.TenantId, Guid.Parse(jwt.Id), Guid.Parse(alvo.UserId), new DateTimeOffset(DateTime.SpecifyKind(jwt.ValidTo, DateTimeKind.Utc)));
            using (var scope = factory.Services.CreateScope())
            {
                await HandlerDaConcessao(scope, c.UnidadeId, concessao, Estado()).Handle(new SetUnidadeCorrenteCommand(b.UnidadeId), CancellationToken.None);
            }

            (await VinculosAtivosDaConcessaoAsync(concessao.ConcessaoId)).ShouldBe(0, "a troca solta o guichê da concessão");
            var colegaToken = (await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(c.UnidadeId))).AccessToken;
            using var colega = _tenant.Cliente(colegaToken);
            using var chamadasDoColega = new Caixa<SenhaChamada>(await ConectarAsync(colegaToken), PafHubMetodos.SenhaChamada);
            using (var selecao = await colega.PostAsJsonAsync("/api/v1/paf/sessao/guiche", new { estacaoId = c.GuicheA, liberarPendencia = false }))
            {
                selecao.StatusCode.ShouldBe(HttpStatusCode.NoContent, await selecao.Content.ReadAsStringAsync());
            }

            var chamada = await PafOperacao.ChamarOkAsync(colega);

            (await chamadasDoColega.EsperarAsync(s => s.AtendimentoId == chamada.AtendimentoId, Espera))
                .ShouldNotBeNull("o grupo do guichê A recebe a chamada do colega");
            await Task.Delay(SilencioEsperado);
            chamadasDoSuporte.Recebidos.ShouldNotContain(s => s.AtendimentoId == chamada.AtendimentoId, "a troca tirou a conexão do suporte do grupo do guichê A");
        }
        finally
        {
            await op.DrenarCaixaDeSaidaAsync();
        }
    }

    // ─── helpers ─────────────────────────────────────────────────────

    /// <summary>Usuário com escopo A (principal) e B, licença em A, e uma prioridade nomeada em cada unidade.</summary>
    private async Task<(UnidadeTeste A, UnidadeTeste B, SeededUser Alvo)> CenarioAsync()
    {
        var a = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var b = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var alvo = await _tenant.CriarUsuarioAsync(a.UnidadeId, b.UnidadeId);
        await _tenant.DarPapelAsync(alvo.UserId, PafPermissions.Prioridades.View);
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        db.Prioridades.AddRange(Prioridade.Criar(a.UnidadeId, "PA", 0), Prioridade.Criar(b.UnidadeId, "PB", 0));
        await db.SaveChangesAsync();
        return (a, b, alvo);
    }

    private async Task<string> IniciarAsync(string alvo)
    {
        using var root = await new AuthHelper(factory).CreateRootAdminClientAsync();
        using var resposta = await root.PostAsJsonAsync($"{Impersonacao}/start", new
        {
            targetUserId = alvo,
            targetTenantId = _tenant.TenantId,
            reason = "teste da unidade da impersonação",
        });
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task<List<string>> NomesAsync(HttpClient cliente, Guid? cabecalho)
    {
        using var resposta = await PafApi.EnviarAsync(cliente, HttpMethod.Get, "/prioridades", cabecalho, null);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        var lista = await resposta.Content.ReadFromJsonAsync<List<PrioridadeDto>>();
        return [.. lista!.Select(p => p.Nome).Order(StringComparer.Ordinal)];
    }

    private async Task VincularConcessaoAsync(Guid estacaoId, Guid concessaoId, DateTimeOffset expiraEm)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        db.SessoesEstacao.Add(SessaoEstacao.CriarParaConcessao(estacaoId, concessaoId, expiraEm));
        await db.SaveChangesAsync();
    }

    private async Task<int> VinculosAtivosDaConcessaoAsync(Guid concessaoId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        return await db.SessoesEstacao.AsNoTracking().CountAsync(v => v.ConcessaoId == concessaoId && v.LiberadoEm == null);
    }

    private EstadoSessao Estado() => new([_multiplexer], NullLogger<EstadoSessao>.Instance);

    private ConcessaoImpersonacao Concessao(SeededUser alvo) =>
        new(_tenant.TenantId, Guid.NewGuid(), Guid.Parse(alvo.UserId), DateTimeOffset.UtcNow.AddMinutes(10));

    private UnidadeDaConcessao Resolvedor(IServiceScope scope, EstadoSessao estado) =>
        new(_tenant.Contexto(scope, unidadeCorrente: null), estado, scope.ServiceProvider.GetRequiredService<EscopoUsuario>(),
            NullLogger<UnidadeDaConcessao>.Instance);

    private SetUnidadeCorrenteCommandHandler HandlerDaConcessao(IServiceScope scope, Guid unidadeCorrente, ConcessaoImpersonacao concessao, EstadoSessao estado)
    {
        var db = _tenant.Contexto(scope, unidadeCorrente);
        var concessaoCorrente = new ConcessaoCorrente();
        concessaoCorrente.Definir(concessao);
        return new SetUnidadeCorrenteCommandHandler(db, new SessaoCorrente(), concessaoCorrente,
            scope.ServiceProvider.GetRequiredService<IUnidadeCorrente>(), estado, scope.ServiceProvider.GetRequiredService<EscopoUsuario>(),
            scope.ServiceProvider.GetRequiredService<Licencas>(), scope.ServiceProvider.GetRequiredService<AvisosTempoReal>(),
            scope.ServiceProvider.GetRequiredService<ConexoesPaf>(), scope.ServiceProvider.GetRequiredService<AtosImpersonacao>());
    }

    private async Task<HubConnection> ConectarAsync(string token)
    {
        var conexao = new HubConnectionBuilder()
            .WithUrl(
                $"http://localhost{PafHubRota.Caminho}?tenant={Uri.EscapeDataString(_tenant.TenantId)}&access_token={Uri.EscapeDataString(token)}",
                options =>
                {
                    // Long polling: TestServer closes a WebSocket before the SignalR handshake (see PafHubTests).
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                })
            .Build();
        _conexoes.Add(conexao);
        await conexao.StartAsync();
        await PafOperacao.ProntaAsync(conexao);
        return conexao;
    }
}
