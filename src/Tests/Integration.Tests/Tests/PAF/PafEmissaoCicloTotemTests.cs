using System.Text.Json;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// The HTTP contract the totem's emission cycle relies on (RF11, requisitos l.403-406; F19): the replay marker
/// on a resend with the same key, and the in-flight 409 the cycle waits on instead of showing an error.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafEmissaoCicloTotemTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    // Mirrors CODIGO_EM_CURSO in clients/dashboard/src/features/paf/totem/emitir-ficha.ts: the front tells the
    // in-flight 409 from a coverage 409 by this code alone.
    private const string CodigoEmCurso = "Idempotency.RequestInProgress";

    private readonly PafTenantCliente _tenant = new(factory, "paf-ciclo");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => _op.DrenarCaixaDeSaidaAsync();

    [Fact]
    public async Task DoisPost_MesmaChave_UmaFicha_ESegundaRespostaMarcadaComoRepetida()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        var chave = Guid.NewGuid().ToString("N");

        using var primeira = await PafOperacao.EmitirAsync(totem, c.Caixa, c.Normal, chave);
        using var repetida = await PafOperacao.EmitirAsync(totem, c.Caixa, c.Normal, chave);

        primeira.StatusCode.ShouldBe(HttpStatusCode.OK, await primeira.Content.ReadAsStringAsync());
        repetida.StatusCode.ShouldBe(HttpStatusCode.OK, await repetida.Content.ReadAsStringAsync());
        primeira.Headers.Contains("Idempotency-Replayed").ShouldBeFalse("a resposta original não é repetida");
        repetida.Headers.GetValues("Idempotency-Replayed").ShouldBe(["true"]);
        var original = (await primeira.Content.ReadFromJsonAsync<FichaEmitidaDto>())!;
        var replay = (await repetida.Content.ReadFromJsonAsync<FichaEmitidaDto>())!;
        replay.FichaId.ShouldBe(original.FichaId);
        replay.Senha.ShouldBe(original.Senha);
        (await _op.ContarAsync(c, db => db.Fichas)).ShouldBe(1);
    }

    [Fact]
    public async Task MesmaChaveEmParalelo_UmaFicha_EOConflitoEmCursoTrazCodigoERetryAfter()
    {
        const int toques = 6;
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        var chave = Guid.NewGuid().ToString("N");

        var respostas = await Task.WhenAll(Enumerable.Range(0, toques)
            .Select(_ => Task.Run(() => PafOperacao.EmitirAsync(totem, c.Caixa, c.Normal, chave))));
        try
        {
            var ok = respostas.Where(r => r.StatusCode == HttpStatusCode.OK).ToList();
            var emCurso = respostas.Where(r => r.StatusCode == HttpStatusCode.Conflict).ToList();
            (ok.Count + emCurso.Count).ShouldBe(toques, "o toque repetido só recebe a ficha ou o 409 de emissão em curso");
            ok.ShouldNotBeEmpty();

            var fichas = new HashSet<Guid>();
            foreach (var r in ok)
            {
                fichas.Add((await r.Content.ReadFromJsonAsync<FichaEmitidaDto>())!.FichaId);
            }

            fichas.Count.ShouldBe(1, "a mesma chave nunca emite segunda ficha");
            foreach (var r in emCurso)
            {
                r.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(1));
                using var problema = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
                problema.RootElement.GetProperty("code").GetString().ShouldBe(CodigoEmCurso);
            }

            (await _op.ContarAsync(c, db => db.Fichas)).ShouldBe(1);
        }
        finally
        {
            foreach (var r in respostas)
            {
                r.Dispose();
            }
        }
    }
}
