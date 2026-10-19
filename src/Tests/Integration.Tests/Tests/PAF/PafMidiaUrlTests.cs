using System.Text.RegularExpressions;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Context;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Midias;
using FSH.Framework.Storage.Services;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Sessions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// M04 against real PostgreSQL and RustFS: the preview URL (<c>GET midias/{id}/url</c>) downloads the file, carries
/// the computed validity, is reused inside the cache window, changes with a file swap, and is refused to an inactive
/// mídia, to a removed envio and outside the unidade. Root operator with the unit header, fresh unidades per test.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed partial class PafMidiaUrlTests(FshWebApplicationFactory factory)
{
    private readonly PafApi _api = new(factory);

    [Fact]
    public async Task MidiaDe3Horas_UrlBaixaOArquivo_ValidadeDe3HorasMaisAMargem()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var bytes = PafMidiaCadastroTests.Bytes(64);
        var midia = await CriarMidiaAsync(root, unidade, bytes, 3 * 3600);

        var antes = DateTimeOffset.UtcNow;
        var url = await UrlOkAsync(root, unidade, midia.Id);
        var depois = DateTimeOffset.UtcNow;

        var validade = TimeSpan.FromHours(3) + TimeSpan.FromMinutes(10);
        SegundosAssinados(url.Url).ShouldBeInRange((long)validade.TotalSeconds - 1, (long)validade.TotalSeconds + FolgaDeRelogio);
        url.ExpiraEm.ShouldBeInRange(antes + validade - ArredondamentoAnunciado - TimeSpan.FromSeconds(1), depois + validade - ArredondamentoAnunciado);
        (await BaixarAsync(url.Url)).ShouldBe((HttpStatusCode.OK, bytes.Length));
    }

    /// <summary>The discriminating cache test: SigV4 dates have 1 s resolution, so two signatures 1.1 s apart differ.</summary>
    [Fact]
    public async Task DoisPedidosNaJanela_MesmaUrl_ETrocaDeArquivoGeraOutra()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var midia = await CriarMidiaAsync(root, unidade, PafMidiaCadastroTests.Bytes(16), 30);

        var primeira = await UrlOkAsync(root, unidade, midia.Id);
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        var segunda = await UrlOkAsync(root, unidade, midia.Id);

        segunda.ShouldBe(primeira, "the second request inside the cache window gets the cached URL and expiry");

        var troca = await PafApi.OkAsync<EnvioMidiaDto>(root, HttpMethod.Post, $"/midias/{midia.Id}/envios", unidade,
            new PedirEnvioTrocaMidiaCommand(midia.Id, "nova.mp4", "video/mp4"));
        (await PafMidiaCadastroTests.EnviarBytesAsync(troca, PafMidiaCadastroTests.Bytes(48), "video/mp4")).ShouldBe(HttpStatusCode.OK);
        await PafMidiaCadastroTests.MidiaOkAsync(root, HttpMethod.Put, $"/midias/{midia.Id}/arquivo", unidade,
            new TrocarArquivoMidiaCommand(midia.Id, troca.EnvioId, midia.Versao));

        var trocada = await UrlOkAsync(root, unidade, midia.Id);
        trocada.Url.ShouldNotBe(primeira.Url);
        (await BaixarAsync(trocada.Url)).ShouldBe((HttpStatusCode.OK, 48), "the swap's object, not the cached old one");
    }

    [Fact]
    public async Task VintePedidosParalelos_TodosOk_UmaUrlSo()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var midia = await CriarMidiaAsync(root, unidade, PafMidiaCadastroTests.Bytes(16), 15);

        var urls = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => UrlOkAsync(root, unidade, midia.Id)));

        urls.Select(u => u.Url).Distinct().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Inativa409_EnvioRemovido409_OutraUnidadeEIdDesconhecido404()
    {
        using var root = await _api.RootAsync();
        var unidadeA = await _api.CriarUnidadeAsync(root);
        var unidadeB = await _api.CriarUnidadeAsync(root);
        var midia = await CriarMidiaAsync(root, unidadeA, PafMidiaCadastroTests.Bytes(16), 20);

        (await StatusAsync(root, unidadeB, midia.Id)).ShouldBe(HttpStatusCode.NotFound, "another unidade of the same caller");
        (await StatusAsync(root, unidadeA, Guid.CreateVersion7())).ShouldBe(HttpStatusCode.NotFound);

        await PafMidiaCadastroTests.MidiaOkAsync(root, HttpMethod.Post, $"/midias/{midia.Id}/disable", unidadeA, null);
        using (var inativa = await PafApi.EnviarAsync(root, HttpMethod.Get, $"/midias/{midia.Id}/url", unidadeA, null))
        {
            inativa.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await inativa.Content.ReadAsStringAsync()).ShouldContain("inativa");
        }

        await PafMidiaCadastroTests.MidiaOkAsync(root, HttpMethod.Post, $"/midias/{midia.Id}/enable", unidadeA, null);
        await UrlOkAsync(root, unidadeA, midia.Id);

        await NoBancoAsync(unidadeA, db => db.Database.ExecuteSqlAsync(
            $"""UPDATE paf."EnviosMidia" SET "RemovidoEm" = now() WHERE "Id" = {midia.EnvioId} AND "TenantId" = {TestConstants.RootTenantId}"""));
        (await StatusAsync(root, unidadeA, midia.Id)).ShouldBe(HttpStatusCode.Conflict, "a removed envio has no object to read, even with a cached URL");
    }

    /// <summary>
    /// Expiry through the service with the host's real store, cache and request context, and only the option values
    /// shrunk (the shared host cannot be rebuilt with other options): 1 s exhibition, 1 s margin, 3 s floor = 3 s.
    /// </summary>
    [Fact]
    public async Task UrlExpira_DepoisDaValidadeCalculada()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var criada = await CriarMidiaAsync(root, unidade, PafMidiaCadastroTests.Bytes(16), 1);
        var raiz = await RaizAsync();
        var opcoes = new OpcoesMidia { MargemLeitura = TimeSpan.FromSeconds(1), PisoValidadeUrlLeitura = TimeSpan.FromSeconds(3) };

        using var scope = factory.Services.CreateScope();
        var (leitura, db) = Servico(scope, raiz, unidade, opcoes, scope.ServiceProvider.GetRequiredService<HybridCache>(), NullLogger<UrlLeituraMidia>.Instance);
        var midia = await db.Midias.AsNoTracking().SingleAsync(m => m.Id == criada.Id);

        var url = (await leitura.GerarAsync(midia, TimeSpan.FromSeconds(1), default)).ShouldNotBeNull();

        SegundosAssinados(url.Url).ShouldBeInRange(2, 3 + FolgaDeRelogio);
        (await BaixarAsync(url.Url)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var espera = url.ExpiraEm - DateTimeOffset.UtcNow + ArredondamentoAnunciado + TimeSpan.FromSeconds(FolgaDeRelogio + 1);
        if (espera > TimeSpan.Zero)
        {
            await Task.Delay(espera);
        }

        (await BaixarAsync(url.Url)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the store refuses the URL past its validity");
    }

    /// <summary>
    /// An exhibition longer than the R2 cap: the URL is still SigV4 (asked for exactly 7 days, the SDK's skew-corrected
    /// clock pushed it over 604800 s and it fell back to SigV2 here) and never above 604800 s, and the store serves it.
    /// </summary>
    [Fact]
    public async Task ExibicaoAcimaDoTeto_UrlSigV4NoTeto_ArmazenamentoServe()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var criada = await CriarMidiaAsync(root, unidade, PafMidiaCadastroTests.Bytes(16), 86_400);
        var raiz = await RaizAsync();

        using var scope = factory.Services.CreateScope();
        var (leitura, db) = Servico(scope, raiz, unidade, new OpcoesMidia(), scope.ServiceProvider.GetRequiredService<HybridCache>(), NullLogger<UrlLeituraMidia>.Instance);
        var midia = await db.Midias.AsNoTracking().SingleAsync(m => m.Id == criada.Id);

        var url = (await leitura.GerarAsync(midia, TimeSpan.FromDays(1) * 8, default)).ShouldNotBeNull();

        SegundosAssinados(url.Url).ShouldBeInRange((long)UrlLeituraMidia.TetoValidade.TotalSeconds - 1, 604_800);
        (await BaixarAsync(url.Url)).ShouldBe((HttpStatusCode.OK, 16));
    }

    /// <summary>
    /// RNF05 on the cache: a cache that never answers (what a Valkey stalled past its own deadlines looks like) is cut
    /// at the PAF deadline, and the URL is signed without it, with a warning that names the mídia and not the URL.
    /// </summary>
    [Fact]
    public async Task CacheParado_AssinaSemCacheDentroDoPrazo_ComAviso()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var criada = await CriarMidiaAsync(root, unidade, PafMidiaCadastroTests.Bytes(16), 30);
        var raiz = await RaizAsync();
        var avisos = new LoggerColetor();

        using var scope = factory.Services.CreateScope();
        var (leitura, db) = Servico(scope, raiz, unidade, new OpcoesMidia(), new CacheParado(), avisos);
        var midia = await db.Midias.AsNoTracking().SingleAsync(m => m.Id == criada.Id);

        var relogio = System.Diagnostics.Stopwatch.StartNew();
        var url = (await leitura.GerarAsync(midia, TimeSpan.FromSeconds(30), default)).ShouldNotBeNull();
        relogio.Stop();

        relogio.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
        (await BaixarAsync(url.Url)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var aviso = avisos.Avisos.ShouldHaveSingleItem();
        aviso.ShouldContain(midia.Id.ToString());
        aviso.ShouldNotContain(midia.ChaveObjeto);
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private static readonly TimeSpan ArredondamentoAnunciado = TimeSpan.FromSeconds(2);

    // The AWS SDK signs with its clock corrected to the store's (RustFS in a container ran 1-2 s behind the host), so
    // X-Amz-Expires may exceed the validity asked by that skew; the instant it expires is the same.
    private const long FolgaDeRelogio = 5;

    private async Task<AppTenantInfo> RaizAsync()
    {
        using var scope = factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId)).ShouldNotBeNull();
    }

    // Synchronous on purpose: the Finbuckle context is an AsyncLocal that would not flow back from an async helper.
    private static (UrlLeituraMidia Leitura, PafDbContext Db) Servico(
        IServiceScope scope, AppTenantInfo raiz, Guid unidade, OpcoesMidia valores, HybridCache cache, ILogger<UrlLeituraMidia> logger)
    {
        var servicos = scope.ServiceProvider;
        servicos.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(raiz);
        servicos.GetRequiredService<IUnidadeCorrente>().Definir(unidade);
        var db = servicos.GetRequiredService<PafDbContext>();
        var opcoes = Options.Create(valores);
        var leitura = new UrlLeituraMidia(
            db,
            new ArmazenamentoMidia(servicos.GetRequiredService<IStorageService>(), opcoes),
            cache,
            servicos.GetRequiredService<IRequestContext>(),
            servicos.GetRequiredService<IMultiTenantContextAccessor<AppTenantInfo>>(),
            TimeProvider.System,
            opcoes,
            logger);
        return (leitura, db);
    }

    /// <summary>A cache whose every call waits for its token: only the caller's deadline ends it.</summary>
    private sealed class CacheParado : HybridCache
    {
        public override async ValueTask<T> GetOrCreateAsync<TState, T>(string key, TState state, Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null, IEnumerable<string>? tags = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public override ValueTask SetAsync<T>(string key, T value, HybridCacheEntryOptions? options = null, IEnumerable<string>? tags = null, CancellationToken cancellationToken = default) =>
            new(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));

        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class LoggerColetor : ILogger<UrlLeituraMidia>
    {
        public List<string> Avisos { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Avisos.Add(formatter(state, exception));
            }
        }
    }


    internal static async Task<MidiaComEnvio> CriarMidiaAsync(HttpClient client, Guid? unidade, byte[] bytes, int duracaoSegundos)
    {
        var envio = await PafMidiaCadastroTests.PedirEnvioAsync(client, unidade, "abertura.mp4", "video/mp4");
        (await PafMidiaCadastroTests.EnviarBytesAsync(envio, bytes, "video/mp4")).ShouldBe(HttpStatusCode.OK);
        var midia = await PafMidiaCadastroTests.MidiaOkAsync(client, HttpMethod.Post, "/midias", unidade,
            new CreateMidiaCommand(envio.EnvioId, "Abertura", duracaoSegundos));
        return new MidiaComEnvio(midia.Id, envio.EnvioId, midia.Versao, midia.UnidadeId);
    }

    internal static Task<UrlLeituraMidiaDto> UrlOkAsync(HttpClient client, Guid? unidade, Guid midiaId) =>
        PafApi.OkAsync<UrlLeituraMidiaDto>(client, HttpMethod.Get, $"/midias/{midiaId}/url", unidade, null);

    internal static async Task<HttpStatusCode> StatusAsync(HttpClient client, Guid? unidade, Guid midiaId)
    {
        using var resposta = await PafApi.EnviarAsync(client, HttpMethod.Get, $"/midias/{midiaId}/url", unidade, null);
        return resposta.StatusCode;
    }

    private static async Task<(HttpStatusCode StatusCode, int Tamanho)> BaixarAsync(Uri url)
    {
        using var http = new HttpClient { Timeout = PafMidiaCadastroTests.Prazo };
        using var resposta = await http.GetAsync(url);
        var corpo = await resposta.Content.ReadAsByteArrayAsync();
        return (resposta.StatusCode, corpo.Length);
    }

    private static long SegundosAssinados(Uri url)
    {
        var assinatura = ExpiresDaAssinatura().Match(url.Query);
        assinatura.Success.ShouldBeTrue($"SigV4 query signature expected, got the parameters {string.Join(",", url.Query.TrimStart('?').Split('&').Select(p => p.Split('=')[0]))}");
        return long.Parse(assinatura.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<T> NoBancoAsync<T>(Guid unidade, Func<PafDbContext, Task<T>> consulta)
    {
        using var scope = factory.Services.CreateScope();
        var raiz = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(raiz!);
        scope.ServiceProvider.GetRequiredService<IUnidadeCorrente>().Definir(unidade);
        return await consulta(scope.ServiceProvider.GetRequiredService<PafDbContext>());
    }

    [GeneratedRegex(@"[?&]X-Amz-Expires=(\d+)")]
    private static partial Regex ExpiresDaAssinatura();

    internal sealed record MidiaComEnvio(Guid Id, Guid EnvioId, int Versao, Guid UnidadeId);
}

/// <summary>M04 in a client tenant: permission (403), the other tenant's and the other unidade's mídia (404).</summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafMidiaUrlTenantTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private readonly PafTenantCliente _tenant = new(factory, "paf-m04");

    public Task InitializeAsync() => _tenant.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task LeitorDaUnidade200_SemView403_OutraUnidade404_OutroTenant404()
    {
        var api = new PafApi(factory);
        using var root = await api.RootAsync();
        var unidadeRaiz = await api.CriarUnidadeAsync(root);
        var midiaRaiz = await PafMidiaUrlTests.CriarMidiaAsync(root, unidadeRaiz, PafMidiaCadastroTests.Bytes(16), 10);
        var midia = await PafMidiaUrlTests.CriarMidiaAsync(_tenant.Admin, null, PafMidiaCadastroTests.Bytes(16), 10);
        midia.UnidadeId.ShouldBe(_tenant.UnidadeInicial);

        var leitor = await _tenant.CriarUsuarioAsync(_tenant.UnidadeInicial);
        await _tenant.DarPapelAsync(leitor.UserId, PafPermissions.Midias.View);
        using var comView = _tenant.Cliente((await _tenant.EntrarAsync(leitor)).AccessToken);
        (await PafMidiaUrlTests.UrlOkAsync(comView, null, midia.Id)).Url.ShouldNotBeNull();
        (await PafMidiaUrlTests.StatusAsync(comView, null, midiaRaiz.Id)).ShouldBe(HttpStatusCode.NotFound, "the root tenant's mídia");

        var semView = await _tenant.CriarUsuarioAsync(_tenant.UnidadeInicial);
        await _tenant.DarPapelAsync(semView.UserId, PafPermissions.Midias.Create, PafPermissions.Midias.Update);
        using var semLeitura = _tenant.Cliente((await _tenant.EntrarAsync(semView)).AccessToken);
        (await PafMidiaUrlTests.StatusAsync(semLeitura, null, midia.Id)).ShouldBe(HttpStatusCode.Forbidden);

        var outra = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var leitorDeOutra = await _tenant.CriarUsuarioAsync(outra.UnidadeId);
        await _tenant.DarPapelAsync(leitorDeOutra.UserId, PafPermissions.Midias.View);
        using var deOutraUnidade = _tenant.Cliente((await _tenant.EntrarAsync(leitorDeOutra)).AccessToken);
        (await PafMidiaUrlTests.StatusAsync(deOutraUnidade, null, midia.Id)).ShouldBe(HttpStatusCode.NotFound, "same tenant, another unidade");
    }
}
