using System.Diagnostics;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Sessao;
using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR.Client;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Aviso <c>SessaoInvalidada</c> (seção 7, "Revogar sessão encerra a conexão"; RF22): a revogação manda o motivo às
/// conexões da sessão revogada, e só a elas, antes de o servidor fechá-las; a revogação nascida no kit chega pelo
/// evento de S01 e, perdido o evento, pelo ciclo de renovação. O host do harness sobe sem Valkey (uma instância);
/// o caminho entre instâncias e o descarte do aviso repetido pelo id da sessão estão em PafRegistroConexoesTests.
/// O cliente que ignora o aviso e invoca o hub recebe HubException: PafHubConexoesTests.Filtro_ChamadaComSessaoRevogada_AbortaERecusa.
/// Aqui o transporte é long polling, e cada poll passa de novo pela checagem de sessão: revogada a sessão, o poll que
/// ainda não estava parado no servidor volta 401 e o aviso fica na fila. Só cabe aqui o cenário em que o cliente está
/// ocioso quando a revogação acontece; a revogação logo depois de conectar (RevogacaoSessao, ciclo de renovação) está
/// em PAF.Kestrel.Tests.PafHubWebSocketTests, sobre WebSocket, o transporte do front.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafSessaoInvalidadaTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SilencioEsperado = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan LimiteDerrubada = TimeSpan.FromSeconds(1);

    private readonly PafTenantCliente _tenant = new(factory, "paf-si");
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
    public async Task LoginEmOutraMaquina_AvisaEDerrubaAAnteriorEmMenosDeUmSegundo_ENaoAvisaANova()
    {
        var c = await _op.CriarAsync();
        var usuario = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        var tokenA = (await _tenant.EntrarAsync(usuario)).AccessToken;
        var maquinaA = await ConectarAsync(tokenA);
        using var avisosA = new Caixa<SessaoInvalidada>(maquinaA, SessaoInvalidada.Metodo);
        var fechouA = Fechamento(maquinaA);

        // Measured from B's login answer, when the revocation is committed: the login's own cost (password hash,
        // licence) is not the drop latency the criterion bounds.
        var tokenB = (await _tenant.EntrarAsync(usuario)).AccessToken;
        var relogio = Stopwatch.StartNew();
        (await Task.WhenAny(fechouA, Task.Delay(Espera))).ShouldBe(fechouA, "a sessão única fecha a conexão da máquina A");
        relogio.Stop();

        relogio.Elapsed.ShouldBeLessThan(LimiteDerrubada, "a revogação nascida no PAF não espera o evento do kit");
        var aviso = avisosA.Recebidos.ShouldHaveSingleItem("o aviso chega antes do fechamento, uma vez");
        aviso.SessaoId.ShouldBe(PafTenantCliente.SessaoDoToken(tokenA));
        aviso.Motivo.ShouldBe(GenerateTokenLicenseBehavior.MensagemSessaoUnica);

        // B is connected and in the user group when the kit's copy of A's revocation is delivered (seção 7): it must
        // not reach B, which would log B out by itself.
        var maquinaB = await ConectarAsync(tokenB);
        using var avisosB = new Caixa<SessaoInvalidada>(maquinaB, SessaoInvalidada.Metodo);
        await _op.DrenarCaixaDeSaidaAsync();
        await Task.Delay(SilencioEsperado);

        avisosB.Recebidos.ShouldBeEmpty("o aviso vai às conexões da sessão revogada, nunca ao grupo do usuário");
        maquinaB.State.ShouldBe(HubConnectionState.Connected);
        avisosA.Recebidos.Count.ShouldBe(1, "o evento do kit depois da revogação do PAF não gera segundo aviso");
    }

    [Fact]
    public async Task RevogacaoPelaTelaDoKit_AvisaEDerrubaDepoisDoEvento_ESoltaALicenca()
    {
        var c = await _op.CriarAsync();
        var usuario = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        var token = (await _tenant.EntrarAsync(usuario)).AccessToken;
        var sessaoId = PafTenantCliente.SessaoDoToken(token);
        var conexao = await ConectarAsync(token);
        using var avisos = new Caixa<SessaoInvalidada>(conexao, SessaoInvalidada.Metodo);
        var fechou = Fechamento(conexao);
        var controle = await ConectarAsync(await _op.CredencialAsync(c.UnidadeId, c.Painel, PafPapeisEstacao.Painel));

        using (var resposta = await _tenant.Admin.DeleteAsync($"{TestConstants.IdentityBasePath}/users/{usuario.UserId}/sessions/{sessaoId}"))
        {
            resposta.IsSuccessStatusCode.ShouldBeTrue(await resposta.Content.ReadAsStringAsync());
        }

        // The kit screen writes only the UserSession: before the event nothing tells the screen (seção 7).
        await Task.Delay(SilencioEsperado);
        avisos.Recebidos.ShouldBeEmpty("sem o evento, a tela ainda não sabe");

        await _op.DrenarCaixaDeSaidaAsync();

        (await avisos.EsperarAsync(a => a.SessaoId == sessaoId, Espera)).ShouldNotBeNull()
            .Motivo.ShouldBe(SessaoRevogadaPafHandler.MensagemSessaoEncerrada);
        (await Task.WhenAny(fechou, Task.Delay(Espera))).ShouldBe(fechou, "o consumidor do evento fecha a conexão");
        (await _tenant.AtividadesAsync(Guid.Parse(usuario.UserId))).ShouldBeEmpty("a licença sai junto, sem esperar a marca vencer");
        controle.State.ShouldBe(HubConnectionState.Connected, "conexões de outras credenciais seguem abertas");
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private async Task<HubConnection> ConectarAsync(string token)
    {
        var conexao = await _op.ConectarAsync(token);
        _conexoes.Add(conexao);
        return conexao;
    }

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
}
