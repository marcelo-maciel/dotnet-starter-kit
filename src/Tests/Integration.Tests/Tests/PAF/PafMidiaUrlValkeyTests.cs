using System.Diagnostics;
using System.Text;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Midias;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// M04 on the host with a pausable Valkey and the kit's LOCAL store (no S3 here): the local provider answers a
/// server-relative path, which the API resolves against its own origin (<c>OriginOptions:OriginUrl</c>); and with the
/// Valkey paused the URL is still signed, past the cache, with the cache failure logged in the host log.
/// </summary>
[Collection(PafHostValkeyColecao.Nome)]
public sealed class PafMidiaUrlValkeyTests(PafHostValkey host) : IAsyncLifetime
{
    // Valkey paused: the kit's own calls on the path (tenant cache, permissions) cost their 500 ms deadlines, plus the
    // PAF cache deadline of 1 s (UrlLeituraMidia.PrazoCache) before signing without it.
    private static readonly TimeSpan TetoSemValkey = TimeSpan.FromSeconds(5);

    private static readonly string[] AvisosDeCache = ["Cache backend read failure", "PAF media read URL signed without the cache"];

    private bool _valkeyPausado;

    public Task InitializeAsync() => Task.CompletedTask;

    // A failed assertion must not leave the shared Valkey paused for the next test of the collection.
    public async Task DisposeAsync()
    {
        if (_valkeyPausado)
        {
            await host.RetomarValkeyAsync();
        }
    }

    [Fact]
    public async Task ArmazenamentoLocal_UrlRelativaResolvidaContraAApi_EValkeyParadoAindaGeraComAviso()
    {
        using var root = await ClienteRootAsync();
        var (unidade, comValkey, semValkey) = await CenarioAsync();

        var url = await PafMidiaUrlTests.UrlOkAsync(root, unidade, comValkey.Id);
        url.Url.IsAbsoluteUri.ShouldBeTrue();
        url.Url.AbsoluteUri.ShouldBe($"http://localhost/{comValkey.ChaveObjeto}");

        var inicioDoLog = TamanhoDoLog();
        await host.PausarValkeyAsync();
        _valkeyPausado = true;

        var relogio = Stopwatch.StartNew();
        var semCache = await PafMidiaUrlTests.UrlOkAsync(root, unidade, semValkey.Id);
        relogio.Stop();

        semCache.Url.AbsoluteUri.ShouldBe($"http://localhost/{semValkey.ChaveObjeto}");
        relogio.Elapsed.ShouldBeLessThan(TetoSemValkey);
        // Which warning depends on the stalled read: under the PAF deadline (1 s) HybridCache absorbs the L2 timeout
        // itself, logs it at Error and calls the factory; past it the PAF signs without the cache and logs its own.
        // The second path alone is pinned by PafMidiaUrlTests.CacheParado_AssinaSemCacheDentroDoPrazo_ComAviso.
        var log = await EsperarLogAsync(inicioDoLog, AvisosDeCache);
        AvisosDeCache.ShouldContain(aviso => log.Contains(aviso, StringComparison.Ordinal), log);
        log.ShouldNotContain(semValkey.ChaveObjeto, Case.Insensitive, "the URL (a credential with S3) is never logged");

        await host.RetomarValkeyAsync();
        _valkeyPausado = false;
    }

    // ─── cenário ─────────────────────────────────────────────────

    /// <summary>
    /// A unidade in the root tenant, in the root admin's scope (the unit header needs it), with two active mídias on
    /// confirmed envios, written straight in the database: this host has no S3 to receive a real upload.
    /// </summary>
    private async Task<(Guid Unidade, Midia ComValkey, Midia SemValkey)> CenarioAsync()
    {
        using var scope = host.Services.CreateScope();
        var raiz = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(raiz!);
        var db = scope.ServiceProvider.GetRequiredService<PafDbContext>();
        var adminId = await AdminRaizIdAsync(scope);

        var unidade = Unidade.Criar($"Loja {Guid.NewGuid():N}", documentoFiscal: null);
        db.Unidades.Add(unidade);
        int? ultimaOrdem = await db.UsuariosUnidade.Where(u => u.UsuarioId == adminId).MaxAsync(u => (int?)u.Ordem);
        db.UsuariosUnidade.Add(UsuarioUnidade.Criar(adminId, unidade.Id, (ultimaOrdem ?? -1) + 1));
        var midias = new[] { NovaMidia(db, unidade.Id), NovaMidia(db, unidade.Id) };
        await db.SaveChangesAsync();
        foreach (var midia in midias)
        {
            await db.Database.ExecuteSqlAsync(
                $"""UPDATE paf."EnviosMidia" SET "ConfirmadoEm" = now() WHERE "Id" = {midia.EnvioId} AND "TenantId" = {TestConstants.RootTenantId}""");
        }

        return (unidade.Id, midias[0], midias[1]);
    }

    private static Midia NovaMidia(PafDbContext db, Guid unidadeId)
    {
        var formato = FormatosMidia.PorNomeDeArquivo("abertura.mp4")!;
        var chave = ChaveObjetoMidia.NovoEnvio(TestConstants.RootTenantId, unidadeId, formato);
        var envio = EnvioMidia.Criar(chave.EnvioId, unidadeId, chave.Chave, formato.Tipo, formato.Mime);
        var midia = Midia.Criar(envio, "Abertura", 30);
        db.EnviosMidia.Add(envio);
        db.Midias.Add(midia);
        return midia;
    }

    private static async Task<Guid> AdminRaizIdAsync(IServiceScope scope)
    {
        var usuarios = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<FSH.Modules.Identity.Domain.FshUser>>();
        var admin = await usuarios.FindByEmailAsync(TestConstants.RootAdminEmail);
        return Guid.Parse(admin.ShouldNotBeNull().Id);
    }

    private async Task<HttpClient> ClienteRootAsync()
    {
        using var login = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{TestConstants.IdentityBasePath}/token/issue");
        request.Headers.Add("tenant", TestConstants.RootTenantId);
        request.Content = JsonContent.Create(new { email = TestConstants.RootAdminEmail, password = TestConstants.DefaultPassword });
        using var resposta = await login.SendAsync(request);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        var token = (await resposta.Content.ReadFromJsonAsync<TokenResult>())!;

        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        client.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);
        return client;
    }

    // The file sink flushes on its own schedule: poll a few seconds for the line.
    private async Task<string> EsperarLogAsync(long inicio, string[] trechos)
    {
        var relogio = Stopwatch.StartNew();
        var log = LerLogDesde(inicio);
        while (!trechos.Any(t => log.Contains(t, StringComparison.Ordinal)) && relogio.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            log = LerLogDesde(inicio);
        }

        return log;
    }

    private long TamanhoDoLog() => File.Exists(host.ArquivoDeLog) ? new FileInfo(host.ArquivoDeLog).Length : 0;

    private string LerLogDesde(long inicio)
    {
        if (!File.Exists(host.ArquivoDeLog))
        {
            return string.Empty;
        }

        // The sink holds the file open for writing; share it instead of copying.
        using var arquivo = new FileStream(host.ArquivoDeLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        arquivo.Seek(inicio, SeekOrigin.Begin);
        using var leitor = new StreamReader(arquivo, Encoding.UTF8);
        return leitor.ReadToEnd();
    }
}
