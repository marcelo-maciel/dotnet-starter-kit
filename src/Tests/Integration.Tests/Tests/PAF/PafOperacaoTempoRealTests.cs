using System.Collections.Concurrent;
using System.Diagnostics;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Painel;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit.Abstractions;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Totem → guichê → painel through the real host and PafHub (RF21, RNF03, RNF18, seção 8 item 6). The hub
/// client runs over long polling on TestServer; WebSocket is not exercised here (see PafHubTests).
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafOperacaoTempoRealTests(FshWebApplicationFactory factory, ITestOutputHelper saida) : IAsyncLifetime
{
    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SilencioEsperado = TimeSpan.FromSeconds(1.5);

    private readonly PafTenantCliente _tenant = new(factory, "paf-rt");
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
    public async Task PontaAPonta_TotemEmiteGuicheChama_PainelDaFilaRecebeASenha()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        var painel = await ConectarAsync(await _op.CredencialAsync(c.UnidadeId, c.Painel, PafPapeisEstacao.Painel));
        var outroPainel = await ConectarAsync(await _op.CredencialAsync(c.UnidadeId, c.PainelProtocolo, PafPapeisEstacao.Painel));
        using var filas = new Caixa<FilaAtualizada>(painel, PafHubMetodos.FilaAtualizada);
        using var chamadas = new Caixa<SenhaChamada>(painel, PafHubMetodos.SenhaChamada);
        using var rechamadas = new Caixa<SenhaRechamada>(painel, PafHubMetodos.SenhaRechamada);
        using var atualizacoes = new Caixa<AtendimentoAtualizado>(painel, PafHubMetodos.AtendimentoAtualizado);
        using var chamadasNoOutro = new Caixa<SenhaChamada>(outroPainel, PafHubMetodos.SenhaChamada);

        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        (await filas.EsperarAsync(f => f.FilaId == c.Caixa, Espera)).ShouldNotBeNull("a emissão avisa a unidade que a fila mudou");

        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var chamada = await PafOperacao.ChamarOkAsync(atendente);

        var recebida = await chamadas.EsperarAsync(s => s.AtendimentoId == chamada.AtendimentoId, Espera);
        recebida.ShouldNotBeNull("o painel da fila recebe a chamada depois da confirmação");
        recebida.Senha.ShouldBe(ficha.Senha);
        recebida.Versao.ShouldBe(1);
        recebida.EstacaoId.ShouldBe(c.GuicheA);
        recebida.NomeEstacao.ShouldBe("Guichê 1");
        recebida.FichaId.ShouldBe(ficha.FichaId);

        await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "rechamar");
        (await rechamadas.EsperarAsync(s => s.AtendimentoId == chamada.AtendimentoId && s.Versao == 2, Espera)).ShouldNotBeNull();
        await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "iniciar");
        var iniciado = await atualizacoes.EsperarAsync(a => a.AtendimentoId == chamada.AtendimentoId && a.Versao == 3, Espera);
        iniciado.ShouldNotBeNull();
        iniciado.Estado.ShouldBe("EmAtendimento");

        await Task.Delay(SilencioEsperado);
        chamadasNoOutro.Recebidos.ShouldBeEmpty("o painel do Protocolo não chama senha do Caixa");
    }

    [Fact]
    public async Task Reconexao_PainelReconsultaOEstadoComAsChamadasPerdidas()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await _op.AtendenteAsync(c, c.GuicheA);
        using var guicheB = await _op.AtendenteAsync(c, c.GuicheB);
        var tokenPainel = await _op.CredencialAsync(c.UnidadeId, c.Painel, PafPapeisEstacao.Painel);
        using var painelHttp = _tenant.Cliente(tokenPainel);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);

        (await EstadoAsync(painelHttp)).Chamadas.ShouldBeEmpty();
        var conexao = await ConectarAsync(tokenPainel);
        Guid primeiraId;
        using (var chamadas = new Caixa<SenhaChamada>(conexao, PafHubMetodos.SenhaChamada))
        {
            primeiraId = (await PafOperacao.ChamarOkAsync(guicheA)).AtendimentoId;
            (await chamadas.EsperarAsync(s => s.AtendimentoId == primeiraId, Espera)).ShouldNotBeNull();
        }

        // The painel drops off; what happens meanwhile never reaches it as an event.
        await conexao.StopAsync();
        var segunda = await PafOperacao.ChamarOkAsync(guicheB);
        await PafOperacao.AgirOkAsync(guicheA, primeiraId, "rechamar");

        var reconectada = await ConectarAsync(tokenPainel);
        reconectada.State.ShouldBe(HubConnectionState.Connected);
        var estado = await EstadoAsync(painelHttp);

        estado.Chamadas.Count.ShouldBe(2);
        estado.Chamadas[0].AtendimentoId.ShouldBe(primeiraId, "a rechamada sobe para o topo também na reconsulta");
        estado.Chamadas[0].Versao.ShouldBe(2);
        estado.Chamadas[0].UltimaRechamadaEm.ShouldNotBeNull();
        estado.Chamadas[0].NomeEstacao.ShouldBe("Guichê 1");
        estado.Chamadas[1].AtendimentoId.ShouldBe(segunda.AtendimentoId, "a chamada feita com o painel fora aparece na reconsulta");
        estado.Chamadas[1].Versao.ShouldBe(1);
        estado.Chamadas[1].Senha.ShouldBe(segunda.Senha);
        estado.Chamadas[1].Estado.ShouldBe("Chamado");
        estado.Chamadas[1].NomeEstacao.ShouldBe("Guichê 2");
    }

    [Fact]
    public async Task EstadoDoPainel_CredencialDeTotem_Recusa403()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var resposta = await totem.GetAsync(PafOperacao.RotaEstadoPainel);
        resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Latencia_200Chamadas_DaRequisicaoAoPainel_P95AbaixoDeUmSegundo()
    {
        const int chamadas = 200;
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        var painel = await ConectarAsync(await _op.CredencialAsync(c.UnidadeId, c.Painel, PafPapeisEstacao.Painel));

        // First arrival per atendimento: each event arrives twice by design (direct send and outbox).
        var chegadas = new ConcurrentDictionary<Guid, long>();
        var sinais = new ConcurrentDictionary<Guid, TaskCompletionSource>();
        using var inscricao = painel.On<SenhaChamada>(PafHubMetodos.SenhaChamada, s =>
        {
            if (chegadas.TryAdd(s.AtendimentoId, Stopwatch.GetTimestamp()))
            {
                sinais.GetOrAdd(s.AtendimentoId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            }
        });

        for (var i = 0; i < chamadas; i++)
        {
            await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        }

        await _op.EnvelhecerFichasAsync(c.UnidadeId);

        var latencias = new List<double>(chamadas);
        for (var i = 0; i < chamadas; i++)
        {
            var inicio = Stopwatch.GetTimestamp();
            var chamada = await PafOperacao.ChamarOkAsync(atendente);
            var sinal = sinais.GetOrAdd(chamada.AtendimentoId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            (await Task.WhenAny(sinal.Task, Task.Delay(Espera))).ShouldBe(sinal.Task, $"chamada {i + 1} não chegou ao painel em {Espera}");
            latencias.Add(Stopwatch.GetElapsedTime(inicio, chegadas[chamada.AtendimentoId]).TotalMilliseconds);

            // Freeing the guichê is outside the measured window.
            await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "iniciar");
            await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "encerrar");
        }

        latencias.Sort();
        var p50 = Percentil(latencias, 0.50);
        var p95 = Percentil(latencias, 0.95);
        saida.WriteLine($"PAF RNF18 latencia chamada->painel: n={latencias.Count} p50={p50:F1}ms p95={p95:F1}ms max={latencias[^1]:F1}ms (long polling, instancia unica)");
        p95.ShouldBeLessThan(1000, $"p50={p50:F1}ms p95={p95:F1}ms");
    }

    private static double Percentil(List<double> ordenadas, double p) =>
        ordenadas[Math.Clamp((int)Math.Ceiling(p * ordenadas.Count) - 1, 0, ordenadas.Count - 1)];

    private static async Task<EstadoPainelDto> EstadoAsync(HttpClient painel)
    {
        using var resposta = await painel.GetAsync(PafOperacao.RotaEstadoPainel);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<EstadoPainelDto>())!;
    }

    private async Task<HubConnection> ConectarAsync(string token)
    {
        var conexao = await _op.ConectarAsync(token);
        _conexoes.Add(conexao);
        return conexao;
    }
}
