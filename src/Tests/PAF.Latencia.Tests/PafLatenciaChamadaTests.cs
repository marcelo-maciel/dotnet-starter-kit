using System.Diagnostics;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace PAF.Latencia.Tests;

/// <summary>
/// O17, RNF18: from the guichê's call to the senha on the painel, p95 under 1 s, with the D20 reference volume
/// connected (20 unidades × 3 totens, 2 painéis and 8 guichês over real WebSockets) and totens emitting during the
/// measurement. Every (call, painel) pair is a sample. Failure here means the delivery form is revisited
/// (REQ 8.2, l.1059), never that the target moves.
/// </summary>
[Collection(AmbienteLatenciaColecao.Nome)]
public sealed class PafLatenciaChamadaTests(AmbienteLatencia ambiente, ITestOutputHelper saida)
{
    private const int ChamadasMinimas = 200;
    private const int RodadasPorFase = ChamadasMinimas / CargaReferencia.Unidades;

    // A senha that does not arrive within this is a lost delivery, not a slow one.
    private static readonly TimeSpan EsperaMaxima = TimeSpan.FromSeconds(10);

    // Totens keep emitting while the guichês call: one ficha somewhere in the tenant at this interval.
    private static readonly TimeSpan IntervaloEmissao = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan AtrasoInjetado = TimeSpan.FromMilliseconds(50);

    // 10 calls a second across the tenant: D20 is 20 × 600 = 12 000 calls a day, about 0.3 a second over a
    // ten-hour day, so this is a peak some 30 times the average.
    private static readonly TimeSpan IntervaloChamadas = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task UmaInstancia_VolumeDeReferencia_P95AbaixoDe1s()
    {
        var medicao = await MedirAsync("uma instância", RodadasPorFase, guichesPorUnidade: 1, ambiente.PaineisA);

        AfirmarDentroDaMeta(medicao);
    }

    [Fact]
    public async Task ValkeyCom50msInjetados_P95AbaixoDe1s()
    {
        ambiente.Retransmissor.Atraso = AtrasoInjetado;
        try
        {
            // The relay really holds the replies: a PING from the host's own multiplexer pays the delay.
            var valkey = ambiente.InstanciaA.Services.GetServices<IConnectionMultiplexer>().First();
            var ping = await valkey.GetDatabase().PingAsync();
            ping.ShouldBeGreaterThanOrEqualTo(AtrasoInjetado);
            saida.WriteLine($"PING com atraso injetado: {ping.TotalMilliseconds:F1} ms");

            var medicao = await MedirAsync("Valkey +50 ms", RodadasPorFase, guichesPorUnidade: 1, ambiente.PaineisA);

            AfirmarDentroDaMeta(medicao);
        }
        finally
        {
            ambiente.Retransmissor.Atraso = TimeSpan.Zero;
        }
    }

    [Fact]
    public async Task DoisGuichesDaMesmaUnidadeNoMesmoSegundo_AmbosDentroDaMeta()
    {
        // Two guichês of every unidade call together, so each painel gets two senhas in the same second; the
        // deliberate spacing of the announcements (RF21) is the front's and is outside RNF18.
        var medicao = await MedirAsync("dois guichês no mesmo segundo", rodadas: 2, guichesPorUnidade: 2, ambiente.PaineisA);

        medicao.Chamadas.ShouldBe(2 * 2 * CargaReferencia.Unidades);
        medicao.P95DesdeEnvio.ShouldBeLessThan(Medicao.Meta.TotalMilliseconds, medicao.Relatorio());
        medicao.Amostras.Max(a => a.DesdeEnvio).ShouldBeLessThan(Medicao.Meta.TotalMilliseconds, medicao.Relatorio());
    }

    [Fact]
    public async Task DuasInstancias_SaltoPeloBackplane_ReferenciaEntregue()
    {
        // Reference only (REQ 8.1, 06/10/2026: the skeleton is measured on one instance; the hop belongs to the
        // WebSocket spike). The guichês call on A, the painéis listen on B: every senha crosses the Valkey backplane.
        var medicao = await MedirAsync("duas instâncias (referência)", rodadas: 5, guichesPorUnidade: 1, ambiente.PaineisB);

        medicao.Amostras.Count.ShouldBe(medicao.Chamadas * CargaReferencia.PaineisPorUnidade);
    }

    private static void AfirmarDentroDaMeta(Medicao medicao)
    {
        medicao.Chamadas.ShouldBeGreaterThanOrEqualTo(ChamadasMinimas);
        medicao.Amostras.Count.ShouldBe(medicao.Chamadas * CargaReferencia.PaineisPorUnidade);
        medicao.P95DesdeConfirmacao.ShouldBeLessThan(Medicao.Meta.TotalMilliseconds, medicao.Relatorio());
        medicao.P95DesdeEnvio.ShouldBeLessThan(Medicao.Meta.TotalMilliseconds, medicao.Relatorio());
    }

    private async Task<Medicao> MedirAsync(string fase, int rodadas, int guichesPorUnidade, ILookup<Guid, Guid> paineis)
    {
        var porUnidade = ambiente.Carga.Atendentes.GroupBy(a => a.UnidadeId).ToDictionary(g => g.Key, g => g.ToArray());
        var amostras = new List<Amostra>();
        var chamadas = 0;
        using var fimEmissao = new CancellationTokenSource();
        var emissao = EmitirDuranteAsync(fimEmissao.Token);
        try
        {
            for (var rodada = 0; rodada < rodadas; rodada++)
            {
                var guiches = porUnidade.Values
                    .SelectMany(atendentes => Enumerable.Range(0, guichesPorUnidade)
                        .Select(i => atendentes[((rodada * guichesPorUnidade) + i) % atendentes.Length]))
                    .ToArray();
                var resultado = await RodadaAsync(guiches, paineis);
                chamadas += guiches.Length;
                amostras.AddRange(resultado);
            }
        }
        finally
        {
            await fimEmissao.CancelAsync();
            await emissao;
        }

        var medicao = new Medicao(fase, chamadas, amostras);
        saida.WriteLine(medicao.Relatorio());
        return medicao;
    }

    private async Task<List<Amostra>> RodadaAsync(IReadOnlyList<AtendenteCarga> guiches, ILookup<Guid, Guid> paineis)
    {
        // The unidades of a round call one after the other, IntervaloChamadas apart; the guichês of one unidade call
        // together. Twenty simultaneous calls in one tenant would be a burst some 40 times D20's average rate and would
        // measure the host's queueing, not the delivery.
        var ordem = guiches.Select(g => g.UnidadeId).Distinct().Select((u, i) => (u, i)).ToDictionary(x => x.u, x => x.i);
        var chamadas = await Task.WhenAll(guiches.Select(async g =>
        {
            await Task.Delay(IntervaloChamadas * ordem[g.UnidadeId]);
            return await ChamarAsync(g);
        }));
        var amostras = new List<Amostra>();
        foreach (var (atendente, chamada, envio, confirmacao) in chamadas)
        {
            foreach (var conexao in paineis[atendente.UnidadeId])
            {
                var chegada = await ambiente.Chegada(conexao, chamada.AtendimentoId).WaitAsync(EsperaMaxima);
                amostras.Add(new Amostra(
                    Stopwatch.GetElapsedTime(envio, chegada).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(confirmacao, chegada).TotalMilliseconds));
            }
        }

        // Frees each guichê for its next call, outside the measured window.
        await Task.WhenAll(chamadas.Select(c => EncerrarAsync(c.Atendente, c.Chamada.AtendimentoId)));
        return amostras;
    }

    private static async Task<(AtendenteCarga Atendente, ChamadaDto Chamada, long Envio, long Confirmacao)> ChamarAsync(AtendenteCarga atendente)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/paf/guiche/chamar-proximo");
        var envio = Stopwatch.GetTimestamp();
        using var resposta = await atendente.Cliente.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var confirmacao = Stopwatch.GetTimestamp();
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        var chamada = (await resposta.Content.ReadFromJsonAsync<ChamadaDto>())!;
        return (atendente, chamada, envio, confirmacao);
    }

    private static async Task EncerrarAsync(AtendenteCarga atendente, Guid atendimentoId)
    {
        foreach (var passo in new[] { "iniciar", "encerrar" })
        {
            using var resposta = await atendente.Cliente.PostAsync($"/api/v1/paf/guiche/atendimentos/{atendimentoId}/{passo}", content: null);
            resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        }
    }

    private async Task EmitirDuranteAsync(CancellationToken fim)
    {
        var unidades = ambiente.Carga.UnidadesCriadas;
        using var relogio = new PeriodicTimer(IntervaloEmissao);
        var sequencia = 0;
        try
        {
            while (await relogio.WaitForNextTickAsync(fim))
            {
                await ambiente.Carga.EmitirAsync(unidades[sequencia % unidades.Count], sequencia / unidades.Count);
                sequencia++;
            }
        }
        catch (OperationCanceledException)
        {
            // The phase ended: stop emitting.
        }
    }
}
