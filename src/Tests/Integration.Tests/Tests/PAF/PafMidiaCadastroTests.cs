using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Sockets;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Framework.Storage.S3;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Midias;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// M03 against real PostgreSQL and RustFS: the RF03 mídia cadastro through its endpoints, with the browser's direct PUT
/// to the presigned URL in the middle. Root operator with the unit header, so each test owns fresh unidades.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafMidiaCadastroTests(FshWebApplicationFactory factory)
{
    internal const string ListaAceitos = ".mp4, .webm, .jpg";
    internal static readonly TimeSpan Prazo = TimeSpan.FromSeconds(60);

    private readonly PafApi _api = new(factory);

    [Fact]
    public async Task FluxoFelizMp4_EnviaConfirmaListaEditaDesativa_ConfirmarDuasVezesDevolveAMesma()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);

        var envio = await PedirEnvioAsync(root, unidade, "Abertura da Loja.MP4", "video/mp4");
        envio.CabecalhosObrigatorios["Content-Type"].ShouldBe("video/mp4");
        envio.TetoBytes.ShouldBe(500L * 1024 * 1024);
        (await EnviarBytesAsync(envio, Bytes(64), "video/mp4")).ShouldBe(HttpStatusCode.OK);

        var midia = await MidiaOkAsync(root, HttpMethod.Post, "/midias", unidade, new CreateMidiaCommand(envio.EnvioId, "  Abertura ", 30));
        (midia.Descricao, midia.Tipo, midia.DuracaoSegundos, midia.Ativa, midia.Versao, midia.UnidadeId)
            .ShouldBe(("Abertura", "Video", 30, true, 0, unidade));
        (await MidiaOkAsync(root, HttpMethod.Post, "/midias", unidade, new CreateMidiaCommand(envio.EnvioId, "Outra descrição", 99)))
            .ShouldBe(midia, "a second confirmation of the same envio returns the mídia already created");
        var gravado = await EnvioAsync(unidade, envio.EnvioId);
        gravado.ConfirmadoEm.ShouldNotBeNull();
        gravado.RecusadoEm.ShouldBeNull();

        var editada = await MidiaOkAsync(root, HttpMethod.Put, $"/midias/{midia.Id}", unidade, new UpdateMidiaCommand(midia.Id, "Abertura nova", 45, 0));
        (editada.Descricao, editada.DuracaoSegundos, editada.Versao).ShouldBe(("Abertura nova", 45, 1));
        using (var obsoleta = await PafApi.EnviarAsync(root, HttpMethod.Put, $"/midias/{midia.Id}", unidade, new UpdateMidiaCommand(midia.Id, "Velha", 10, 0)))
        {
            obsoleta.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        var desativada = await MidiaOkAsync(root, HttpMethod.Post, $"/midias/{midia.Id}/disable", unidade, null);
        (desativada.Ativa, desativada.Versao).ShouldBe((false, 2));
        (await ListarAsync(root, unidade)).ShouldBe([desativada]);
        var ativada = await MidiaOkAsync(root, HttpMethod.Post, $"/midias/{midia.Id}/enable", unidade, null);
        (ativada.Ativa, ativada.Versao).ShouldBe((true, 3));
    }

    [Theory]
    [InlineData("abertura.avi", "video/x-msvideo")]
    [InlineData("abertura.mov", "video/quicktime")]
    [InlineData("abertura.flv", "video/x-flv")]
    [InlineData("abertura.mp4", "image/png")]
    [InlineData("abertura", "video/mp4")]
    [InlineData("abertura.mp4.avi", "video/mp4")]
    public async Task FormatoForaDoRF03_400ComALista_ENenhumEnvioCriado(string nome, string mime)
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);

        using var resposta = await PafApi.EnviarAsync(root, HttpMethod.Post, "/midias/envios", unidade, new PedirEnvioMidiaCommand(nome, mime));

        resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await resposta.Content.ReadAsStringAsync()).ShouldContain(ListaAceitos);
        (await NoBancoAsync(unidade, db => db.EnviosMidia.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task ConfirmarSemPut_409SemCriarMidiaNemMarcarOEnvio_EDepoisDoPutConfirma()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var envio = await PedirEnvioAsync(root, unidade, "logo.png", "image/png");

        using (var cedo = await PafApi.EnviarAsync(root, HttpMethod.Post, "/midias", unidade, new CreateMidiaCommand(envio.EnvioId, "Logo", 10)))
        {
            cedo.StatusCode.ShouldBe(HttpStatusCode.Conflict, await cedo.Content.ReadAsStringAsync());
        }

        (await NoBancoAsync(unidade, db => db.Midias.CountAsync())).ShouldBe(0);
        var aberto = await EnvioAsync(unidade, envio.EnvioId);
        (aberto.ConfirmadoEm, aberto.RecusadoEm).ShouldBe((null, null));

        (await EnviarBytesAsync(envio, Bytes(16), "image/png")).ShouldBe(HttpStatusCode.OK);
        (await MidiaOkAsync(root, HttpMethod.Post, "/midias", unidade, new CreateMidiaCommand(envio.EnvioId, "Logo", 10))).Tipo.ShouldBe("Imagem");
    }

    [Fact]
    public async Task PutComOutroContentType_RecusadoPeloArmazenamento_EAConfirmacaoNaoAcha()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var envio = await PedirEnvioAsync(root, unidade, "abertura.mp4", "video/mp4");

        (await EnviarBytesAsync(envio, Bytes(16), "video/webm")).ShouldBe(HttpStatusCode.Forbidden, "the Content-Type is part of the signature");

        using var confirmar = await PafApi.EnviarAsync(root, HttpMethod.Post, "/midias", unidade, new CreateMidiaCommand(envio.EnvioId, "Abertura", 10));
        confirmar.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AcimaDoTeto_400EEnvioMarcadoRecusado_NovaConfirmacaoTambem400()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var envio = await PedirEnvioAsync(root, unidade, "fundo.png", "image/png");
        envio.TetoBytes.ShouldBe(20L * 1024 * 1024);
        (await EnviarBytesAsync(envio, Bytes((int)envio.TetoBytes + 1), "image/png")).ShouldBe(HttpStatusCode.OK);

        using (var acima = await PafApi.EnviarAsync(root, HttpMethod.Post, "/midias", unidade, new CreateMidiaCommand(envio.EnvioId, "Fundo", 10)))
        {
            acima.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await acima.Content.ReadAsStringAsync()).ShouldContain("20 MiB");
        }

        var recusado = await EnvioAsync(unidade, envio.EnvioId);
        recusado.RecusadoEm.ShouldNotBeNull("M09 removes refused envios after the lock");
        recusado.ConfirmadoEm.ShouldBeNull();
        (await NoBancoAsync(unidade, db => db.Midias.CountAsync())).ShouldBe(0);

        using var denovo = await PafApi.EnviarAsync(root, HttpMethod.Post, "/midias", unidade, new CreateMidiaCommand(envio.EnvioId, "Fundo", 10));
        denovo.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await denovo.Content.ReadAsStringAsync()).ShouldContain("recusado");
    }

    [Fact]
    public async Task ConfirmacoesConcorrentes_DoMesmoEnvio_UmaMidiaSo()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var envio = await PedirEnvioAsync(root, unidade, "abertura.webm", "video/webm");
        (await EnviarBytesAsync(envio, Bytes(32), "video/webm")).ShouldBe(HttpStatusCode.OK);

        var confirmacoes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            MidiaOkAsync(root, HttpMethod.Post, "/midias", unidade, new CreateMidiaCommand(envio.EnvioId, "Abertura", 30))));

        confirmacoes.Select(m => m.Id).Distinct().ShouldHaveSingleItem();
        (await NoBancoAsync(unidade, db => db.Midias.CountAsync(m => m.EnvioId == envio.EnvioId))).ShouldBe(1);
    }

    [Fact]
    public async Task TrocasConcorrentes_Uma409_PerdedorOrfao_ChaveAntigaContinuaNoArmazenamento()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var original = await PedirEnvioAsync(root, unidade, "abertura.mp4", "video/mp4");
        (await EnviarBytesAsync(original, Bytes(16), "video/mp4")).ShouldBe(HttpStatusCode.OK);
        var midia = await MidiaOkAsync(root, HttpMethod.Post, "/midias", unidade, new CreateMidiaCommand(original.EnvioId, "Abertura", 30));
        var chaveAntiga = (await EnvioAsync(unidade, original.EnvioId)).ChaveObjeto;

        // A swap keeps the type: an image for a video is refused before any upload.
        using (var outroTipo = await PafApi.EnviarAsync(root, HttpMethod.Post, $"/midias/{midia.Id}/envios", unidade, new PedirEnvioTrocaMidiaCommand(Guid.Empty, "logo.png", "image/png")))
        {
            outroTipo.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var a = await PedirTrocaAsync(root, unidade, midia.Id);
        var b = await PedirTrocaAsync(root, unidade, midia.Id);
        (await EnviarBytesAsync(a, Bytes(24), "video/mp4")).ShouldBe(HttpStatusCode.OK);
        (await EnviarBytesAsync(b, Bytes(24), "video/mp4")).ShouldBe(HttpStatusCode.OK);

        var trocas = await Task.WhenAll(new[] { a, b }.Select(async e =>
        {
            using var r = await PafApi.EnviarAsync(root, HttpMethod.Put, $"/midias/{midia.Id}/arquivo", unidade, new TrocarArquivoMidiaCommand(midia.Id, e.EnvioId, 0));
            return (Envio: e.EnvioId, r.StatusCode);
        }));

        trocas.Select(t => t.StatusCode).Order().ShouldBe([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        var vencedor = trocas.Single(t => t.StatusCode == HttpStatusCode.OK).Envio;
        var perdedor = trocas.Single(t => t.StatusCode == HttpStatusCode.Conflict).Envio;
        var atual = (await ListarAsync(root, unidade)).ShouldHaveSingleItem();
        atual.Versao.ShouldBe(1);
        (await NoBancoAsync(unidade, db => db.Midias.Select(m => m.EnvioId).SingleAsync())).ShouldBe(vencedor);
        (await NoBancoAsync(unidade, db => db.Midias.CountAsync(m => m.EnvioId == perdedor))).ShouldBe(0, "the loser's envio is left for the cleanup");
        (await EnvioAsync(unidade, perdedor)).ConfirmadoEm.ShouldBeNull();

        // The same swap sent again is answered with the current state, whatever version it carries.
        (await MidiaOkAsync(root, HttpMethod.Put, $"/midias/{midia.Id}/arquivo", unidade, new TrocarArquivoMidiaCommand(midia.Id, vencedor, 0))).Versao.ShouldBe(1);

        using var prazo = new CancellationTokenSource(Prazo);
        var s3 = factory.Services.GetRequiredService<IAmazonS3>();
        (await s3.GetObjectMetadataAsync(FshWebApplicationFactory.S3Bucket, chaveAntiga, prazo.Token)).ContentLength.ShouldBe(16, "the old object is never overwritten nor removed by the swap");
    }

    [Fact]
    public async Task OutraUnidade_404EmTodaEscrita_EListaVazia()
    {
        using var root = await _api.RootAsync();
        var unidadeA = await _api.CriarUnidadeAsync(root);
        var unidadeB = await _api.CriarUnidadeAsync(root);
        var envio = await PedirEnvioAsync(root, unidadeA, "abertura.mp4", "video/mp4");
        (await EnviarBytesAsync(envio, Bytes(16), "video/mp4")).ShouldBe(HttpStatusCode.OK);
        var outro = await PedirEnvioAsync(root, unidadeA, "abertura.mp4", "video/mp4");
        (await EnviarBytesAsync(outro, Bytes(16), "video/mp4")).ShouldBe(HttpStatusCode.OK);
        var midia = await MidiaOkAsync(root, HttpMethod.Post, "/midias", unidadeA, new CreateMidiaCommand(envio.EnvioId, "Abertura", 30));

        (HttpMethod Metodo, string Rota, object? Corpo)[] escritas =
        [
            (HttpMethod.Post, "/midias", new CreateMidiaCommand(outro.EnvioId, "Roubada", 10)),
            (HttpMethod.Put, $"/midias/{midia.Id}", new UpdateMidiaCommand(midia.Id, "Roubada", 10, 0)),
            (HttpMethod.Put, $"/midias/{midia.Id}/arquivo", new TrocarArquivoMidiaCommand(midia.Id, outro.EnvioId, 0)),
            (HttpMethod.Post, $"/midias/{midia.Id}/envios", new PedirEnvioTrocaMidiaCommand(midia.Id, "abertura.mp4", "video/mp4")),
            (HttpMethod.Post, $"/midias/{midia.Id}/enable", null),
            (HttpMethod.Post, $"/midias/{midia.Id}/disable", null),
        ];
        foreach (var (metodo, rota, corpo) in escritas)
        {
            using var resposta = await PafApi.EnviarAsync(root, metodo, rota, unidadeB, corpo);
            resposta.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{metodo} {rota}");
        }

        (await ListarAsync(root, unidadeB)).ShouldBeEmpty();
        (await ListarAsync(root, unidadeA)).ShouldBe([midia], "nothing written from the other unidade");
    }

    // ─── helpers ─────────────────────────────────────────────────────

    internal static Task<EnvioMidiaDto> PedirEnvioAsync(HttpClient client, Guid? unidade, string nome, string mime) =>
        PafApi.OkAsync<EnvioMidiaDto>(client, HttpMethod.Post, "/midias/envios", unidade, new PedirEnvioMidiaCommand(nome, mime));

    private static Task<EnvioMidiaDto> PedirTrocaAsync(HttpClient client, Guid unidade, Guid midiaId) =>
        PafApi.OkAsync<EnvioMidiaDto>(client, HttpMethod.Post, $"/midias/{midiaId}/envios", unidade, new PedirEnvioTrocaMidiaCommand(midiaId, "nova.mp4", "video/mp4"));

    internal static Task<MidiaDto> MidiaOkAsync(HttpClient client, HttpMethod metodo, string rota, Guid? unidade, object? corpo) =>
        PafApi.OkAsync<MidiaDto>(client, metodo, rota, unidade, corpo);

    private static Task<List<MidiaDto>> ListarAsync(HttpClient client, Guid unidade) =>
        PafApi.OkAsync<List<MidiaDto>>(client, HttpMethod.Get, "/midias", unidade, null);

    /// <summary>The browser's direct PUT: every signed header verbatim, except the Content-Type the test chooses.</summary>
    internal static async Task<HttpStatusCode> EnviarBytesAsync(EnvioMidiaDto envio, byte[] bytes, string contentType)
    {
        using var http = new HttpClient { Timeout = Prazo };
        using var conteudo = new ByteArrayContent(bytes);
        conteudo.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        using var put = await http.PutAsync(envio.Url, conteudo);
        return put.StatusCode;
    }

    internal static byte[] Bytes(int tamanho)
    {
        var bytes = new byte[tamanho];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes.AsSpan(0, Math.Min(tamanho, 4096)));
        return bytes;
    }

    private Task<EnvioMidia> EnvioAsync(Guid unidade, Guid envioId) =>
        NoBancoAsync(unidade, db => db.EnviosMidia.AsNoTracking().SingleAsync(e => e.Id == envioId));

    /// <summary>Root tenant context with the unidade set, so the reads go through the same filters as the API.</summary>
    private async Task<T> NoBancoAsync<T>(Guid unidade, Func<PafDbContext, Task<T>> consulta)
    {
        using var scope = factory.Services.CreateScope();
        var raiz = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(raiz!);
        scope.ServiceProvider.GetRequiredService<IUnidadeCorrente>().Definir(unidade);
        return await consulta(scope.ServiceProvider.GetRequiredService<PafDbContext>());
    }
}

/// <summary>
/// M03 in a client tenant: permission (403), the other tenant's mídia (404) and the impersonation act of the raw
/// confirmation UPDATE (D28).
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafMidiaCadastroTenantTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private readonly PafTenantCliente _tenant = new(factory, "paf-m03");

    public Task InitializeAsync() => _tenant.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SoComView_403NasEscritas_EMidiaDeOutroTenant404()
    {
        var api = new PafApi(factory);
        using var root = await api.RootAsync();
        var unidadeRaiz = await api.CriarUnidadeAsync(root);
        var envioRaiz = await PafMidiaCadastroTests.PedirEnvioAsync(root, unidadeRaiz, "abertura.mp4", "video/mp4");
        (await PafMidiaCadastroTests.EnviarBytesAsync(envioRaiz, PafMidiaCadastroTests.Bytes(16), "video/mp4")).ShouldBe(HttpStatusCode.OK);
        var midiaRaiz = await PafMidiaCadastroTests.MidiaOkAsync(root, HttpMethod.Post, "/midias", unidadeRaiz, new CreateMidiaCommand(envioRaiz.EnvioId, "Abertura", 30));

        var leitor = await _tenant.CriarUsuarioAsync(_tenant.UnidadeInicial);
        await _tenant.DarPapelAsync(leitor.UserId, PafPermissions.Midias.View);
        using var soLeitura = _tenant.Cliente((await _tenant.EntrarAsync(leitor)).AccessToken);

        (await PafApi.OkAsync<List<MidiaDto>>(soLeitura, HttpMethod.Get, "/midias", null, null)).ShouldBeEmpty();
        foreach (var (metodo, rota, corpo) in Escritas(midiaRaiz.Id, envioRaiz.EnvioId))
        {
            using var resposta = await PafApi.EnviarAsync(soLeitura, metodo, rota, null, corpo);
            resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{metodo} {rota}");
        }

        // The tenant admin holds every Midias permission, and still never reaches the root tenant's rows.
        foreach (var (metodo, rota, corpo) in Escritas(midiaRaiz.Id, envioRaiz.EnvioId).Where(e => e.Rota != "/midias/envios"))
        {
            using var resposta = await PafApi.EnviarAsync(_tenant.Admin, metodo, rota, null, corpo);
            resposta.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{metodo} {rota}");
        }
    }

    [Fact]
    public async Task CodefiImpersonando_PedirEConfirmar_UmAtoPorEscrita()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var alvo = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        await _tenant.DarPapelAsync(alvo.UserId, PafPermissions.Midias.Create);
        using var root = await new AuthHelper(factory).CreateRootAdminClientAsync();
        var token = await IniciarImpersonacaoAsync(root, alvo.UserId);
        var concessao = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Id);
        using var suporte = _tenant.Cliente(token);

        var envio = await PafMidiaCadastroTests.PedirEnvioAsync(suporte, null, "logo.webp", "image/webp");
        (await PafMidiaCadastroTests.EnviarBytesAsync(envio, PafMidiaCadastroTests.Bytes(16), "image/webp")).ShouldBe(HttpStatusCode.OK);
        await PafMidiaCadastroTests.MidiaOkAsync(suporte, HttpMethod.Post, "/midias", null, new CreateMidiaCommand(envio.EnvioId, "Logo", 10));

        using var scope = factory.Services.CreateScope();
        var atos = await _tenant.Contexto(scope, unidade.UnidadeId).AtosImpersonacao.AsNoTracking()
            .Where(a => a.ConcessaoId == concessao).ToListAsync();
        atos.Count.ShouldBe(2, "one act for the envio, one for the confirmation written by raw UPDATE");
        atos.ShouldAllBe(a => a.UnidadeId == unidade.UnidadeId);
    }

    /// <summary>
    /// The support repeats a confirmation and a swap that already went through: the handlers leave without writing,
    /// so each repeat answers with the existing mídia (not 500) and records no act, since nothing was written.
    /// </summary>
    [Fact]
    public async Task CodefiImpersonando_RepeteConfirmacaoETroca_DevolveAMidiaSemNovoAto()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var alvo = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        await _tenant.DarPapelAsync(alvo.UserId, PafPermissions.Midias.Create, PafPermissions.Midias.Update);
        using var root = await new AuthHelper(factory).CreateRootAdminClientAsync();
        var token = await IniciarImpersonacaoAsync(root, alvo.UserId);
        var concessao = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Id);
        using var suporte = _tenant.Cliente(token);

        var envio = await PafMidiaCadastroTests.PedirEnvioAsync(suporte, null, "logo.webp", "image/webp");
        (await PafMidiaCadastroTests.EnviarBytesAsync(envio, PafMidiaCadastroTests.Bytes(16), "image/webp")).ShouldBe(HttpStatusCode.OK);
        var criar = new CreateMidiaCommand(envio.EnvioId, "Logo", 10);
        var midia = await PafMidiaCadastroTests.MidiaOkAsync(suporte, HttpMethod.Post, "/midias", null, criar);
        var aposCriar = await AtosAsync(unidade.UnidadeId, concessao);

        (await PafMidiaCadastroTests.MidiaOkAsync(suporte, HttpMethod.Post, "/midias", null, criar))
            .ShouldBe(midia, "the repeated confirmation returns the mídia already created");
        (await AtosAsync(unidade.UnidadeId, concessao)).ShouldBe(aposCriar, "a repeat that writes nothing records no act");

        var troca = await PafApi.OkAsync<EnvioMidiaDto>(suporte, HttpMethod.Post, $"/midias/{midia.Id}/envios", null,
            new PedirEnvioTrocaMidiaCommand(midia.Id, "logo-novo.webp", "image/webp"));
        (await PafMidiaCadastroTests.EnviarBytesAsync(troca, PafMidiaCadastroTests.Bytes(16), "image/webp")).ShouldBe(HttpStatusCode.OK);
        var trocar = new TrocarArquivoMidiaCommand(midia.Id, troca.EnvioId, midia.Versao);
        var trocada = await PafMidiaCadastroTests.MidiaOkAsync(suporte, HttpMethod.Put, $"/midias/{midia.Id}/arquivo", null, trocar);
        var aposTrocar = await AtosAsync(unidade.UnidadeId, concessao);
        aposTrocar.ShouldBe(aposCriar + 2, "one act for the swap's envio, one for its confirmation");

        (await PafMidiaCadastroTests.MidiaOkAsync(suporte, HttpMethod.Put, $"/midias/{midia.Id}/arquivo", null, trocar))
            .ShouldBe(trocada, "the repeated swap returns the mídia as the first one left it");
        (await AtosAsync(unidade.UnidadeId, concessao)).ShouldBe(aposTrocar);
    }

    private async Task<int> AtosAsync(Guid unidade, Guid concessao)
    {
        using var scope = factory.Services.CreateScope();
        return await _tenant.Contexto(scope, unidade).AtosImpersonacao.CountAsync(a => a.ConcessaoId == concessao);
    }

    private static (HttpMethod Metodo, string Rota, object? Corpo)[] Escritas(Guid midiaId, Guid envioId) =>
    [
        (HttpMethod.Post, "/midias/envios", new PedirEnvioMidiaCommand("abertura.mp4", "video/mp4")),
        (HttpMethod.Post, "/midias", new CreateMidiaCommand(envioId, "Abertura", 10)),
        (HttpMethod.Put, $"/midias/{midiaId}", new UpdateMidiaCommand(midiaId, "Abertura", 10, 0)),
        (HttpMethod.Put, $"/midias/{midiaId}/arquivo", new TrocarArquivoMidiaCommand(midiaId, envioId, 0)),
        (HttpMethod.Post, $"/midias/{midiaId}/enable", null),
        (HttpMethod.Post, $"/midias/{midiaId}/disable", null),
    ];

    private async Task<string> IniciarImpersonacaoAsync(HttpClient ator, string alvo)
    {
        using var resposta = await ator.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/impersonation/start", new
        {
            targetUserId = alvo,
            targetTenantId = _tenant.TenantId,
            reason = "teste do cadastro de mídia",
        });
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }
}

/// <summary>
/// RNF05 with the kit's real S3 provider against a store that accepts the connection and never answers (what a paused
/// container does): the cadastro's deadline cuts it and answers 503, instead of waiting for the S3 client's own timeout.
/// No host involved: the RustFS of the shared collection is not pausable from a test.
/// </summary>
public sealed class PafMidiaArmazenamentoParadoTests
{
    [Fact]
    public async Task ArmazenamentoParado_ConferenciaEUrl_Respondem503DentroDoPrazo()
    {
        var parado = new TcpListener(IPAddress.Loopback, 0);
        parado.Start();
        var aceitas = new List<TcpClient>();
        var aceitando = AceitarSemResponderAsync(parado, aceitas);
        try
        {
            var prazo = TimeSpan.FromSeconds(1);
            using var cliente = ClienteS3(((IPEndPoint)parado.LocalEndpoint).Port);
            var storage = new S3StorageService(cliente, cliente,
                Options.Create(new S3StorageOptions { Bucket = "parado", ServiceUrl = cliente.Config.ServiceURL, ForcePathStyle = true, Region = "us-east-1" }),
                NullLogger<S3StorageService>.Instance);
            var armazenamento = new ArmazenamentoMidia(storage, Options.Create(new OpcoesMidia { PrazoArmazenamento = prazo }));
            var formato = FormatosMidia.PorNomeDeArquivo("abertura.mp4")!;
            var chave = ChaveObjetoMidia.NovoEnvio("tenant-m03", Guid.CreateVersion7(), formato);
            var envio = EnvioMidia.Criar(chave.EnvioId, Guid.CreateVersion7(), chave.Chave, formato.Tipo, formato.Mime);

            var relogio = Stopwatch.StartNew();
            var erro = await Should.ThrowAsync<CustomException>(() => armazenamento.ConferirAsync(envio, default));
            relogio.Stop();

            erro.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            relogio.Elapsed.ShouldBeLessThan(prazo + TimeSpan.FromSeconds(2));
            aceitas.Count.ShouldBeGreaterThan(0, "the HEAD reached the stalled store");

            // Presigning is local to the client: it answers even with the store stalled.
            (await armazenamento.GerarUrlEnvioAsync(envio, default)).Url.ShouldNotBeNull();
        }
        finally
        {
            parado.Stop();
            await aceitando;
            parado.Dispose();
            aceitas.ForEach(c => c.Dispose());
        }
    }

    private static AmazonS3Client ClienteS3(int porta) =>
        new(new BasicAWSCredentials("parado", "parado"), new AmazonS3Config
        {
            ServiceURL = $"http://127.0.0.1:{porta}",
            ForcePathStyle = true,
            Timeout = TimeSpan.FromSeconds(30),
            MaxErrorRetry = 0,
        });

    private static async Task AceitarSemResponderAsync(TcpListener listener, List<TcpClient> aceitas)
    {
        try
        {
            while (true)
            {
                aceitas.Add(await listener.AcceptTcpClientAsync());
            }
        }
        catch (SocketException)
        {
            // Stop() ends the accept loop.
        }
        catch (ObjectDisposedException)
        {
            // Same, where the runtime disposes the socket instead.
        }
    }
}
