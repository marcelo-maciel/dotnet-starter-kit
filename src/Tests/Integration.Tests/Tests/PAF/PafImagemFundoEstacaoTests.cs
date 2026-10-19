using System.Text.Json;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Escopo;
using Integration.Tests.Infrastructure;
using Mediator;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// C17 against real PostgreSQL and RustFS: a background image on the Emissão and Painel blocks, with each text color
/// measured against the image's luminance sample (D16), the mídia checked through the unit filter, a file swap that
/// invalidates the sample, and the read URL the screens receive. Root operator with the unit header, fresh unidades.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafImagemFundoEstacaoTests(FshWebApplicationFactory factory)
{
    // Bands 240..255 only: a light image (white wall, pale logo).
    private const string AmostraClara = "000000000000000000000000000000000000000000000000000000000000FFFF";

    // Bands 0..15 only: a dark image.
    private const string AmostraEscura = "FFFF000000000000000000000000000000000000000000000000000000000000";

    private readonly PafApi _api = new(factory);

    [Fact]
    public async Task Painel_ImagemClara_TextoBrancoRecusado_TextoPretoAceito_EOPainelRecebeAUrl()
    {
        using var root = await _api.RootAsync();
        var (unidade, fila) = await UnidadeComFilaAsync(root);
        var midia = await ImagemAsync(root, unidade, "fundo.png", "image/png");

        var recusa = await RecusaAsync(root, unidade, HttpMethod.Post, "/estacoes",
            Painel(fila, PainelCom(texto: "#FFFFFF", fundo: "#000000", new ImagemFundoDto(midia.Id, midia.Versao, AmostraClara))));
        recusa.ShouldContain("Painel.CorFonteFilas sobre a imagem de fundo");
        recusa.ShouldContain("Painel.CorFonteChamada sobre a imagem de fundo");
        recusa.ShouldContain("Painel.CorFonteAlternada sobre a imagem de fundo");

        var id = await PafApi.OkAsync<Guid>(root, HttpMethod.Post, "/estacoes", unidade,
            Painel(fila, PainelCom(texto: "#000000", fundo: "#FFFFFF", new ImagemFundoDto(midia.Id, midia.Versao, AmostraClara))));

        var gravada = (await PafApi.OkAsync<List<EstacaoDto>>(root, HttpMethod.Get, "/estacoes", unidade, null)).Single(e => e.Id == id);
        gravada.Configuracao!.Painel!.ImagemFundo.ShouldBe(new ImagemFundoDto(midia.Id, midia.Versao, AmostraClara));

        var configuracao = await ConfiguracaoDaEstacaoAsync(unidade, id);
        configuracao.ImagemFundoEmissao.ShouldBeNull("a painel-only station has no Emissão block");
        var url = configuracao.ImagemFundoPainel.ShouldNotBeNull();
        (await BaixarAsync(url.Url)).ShouldBe(HttpStatusCode.OK);
        url.ExpiraEm.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(10), "covers ten re-reads of the screen");
    }

    [Fact]
    public async Task Totem_ImagemClara_CorTextoBrancaRecusada_SemCorTextoUsaPretoEPassa_ImagemEscuraRecusaOPreto()
    {
        using var root = await _api.RootAsync();
        var (unidade, fila) = await UnidadeComFilaAsync(root);
        var midia = await ImagemAsync(root, unidade, "fundo.jpg", "image/jpeg");

        var recusa = await RecusaAsync(root, unidade, HttpMethod.Post, "/estacoes",
            Totem(fila, new BlocoEmissaoDto(null, null, null, null, null, null, null)
            {
                CorTexto = "#FFFFFF",
                ImagemFundo = new ImagemFundoDto(midia.Id, midia.Versao, AmostraClara),
            }));
        recusa.ShouldContain("Emissao.CorTexto sobre a imagem de fundo tem contraste 1.00:1 no pior ponto");

        var id = await PafApi.OkAsync<Guid>(root, HttpMethod.Post, "/estacoes", unidade,
            Totem(fila, new BlocoEmissaoDto(null, null, null, null, null, null, null) { ImagemFundo = new ImagemFundoDto(midia.Id, midia.Versao, AmostraClara) }));
        (await ConfiguracaoDaEstacaoAsync(unidade, id)).ImagemFundoEmissao.ShouldNotBeNull();

        var escura = await RecusaAsync(root, unidade, HttpMethod.Post, "/estacoes",
            Totem(fila, new BlocoEmissaoDto(null, null, null, null, null, null, null) { ImagemFundo = new ImagemFundoDto(midia.Id, midia.Versao, AmostraEscura) }));
        escura.ShouldContain("Emissao.CorTexto sobre a imagem de fundo", customMessage: "the default black title is measured too");

        // Without an image the text color is still checked, against the totem's white.
        var semImagem = await RecusaAsync(root, unidade, HttpMethod.Post, "/estacoes",
            Totem(fila, new BlocoEmissaoDto(null, null, null, null, null, null, null) { CorTexto = "#777777" }));
        semImagem.ShouldContain("Emissao.CorTexto sobre o fundo branco do totem tem contraste 4.47:1");
    }

    [Fact]
    public async Task TrocaDoArquivo_TiraAImagemDasTelas_ESoVoltaComNovaMedicao_QueReavaliaAsCoresGravadas()
    {
        using var root = await _api.RootAsync();
        var (unidade, fila) = await UnidadeComFilaAsync(root);
        var midia = await ImagemAsync(root, unidade, "fundo.png", "image/png");
        var bloco = PainelCom(texto: "#000000", fundo: "#FFFFFF", new ImagemFundoDto(midia.Id, midia.Versao, AmostraClara));
        var id = await PafApi.OkAsync<Guid>(root, HttpMethod.Post, "/estacoes", unidade, Painel(fila, bloco));
        var antes = (await ConfiguracaoDaEstacaoAsync(unidade, id)).ImagemFundoPainel.ShouldNotBeNull();

        var troca = await PafApi.OkAsync<EnvioMidiaDto>(root, HttpMethod.Post, $"/midias/{midia.Id}/envios", unidade,
            new PedirEnvioTrocaMidiaCommand(midia.Id, "escura.webp", "image/webp"));
        (await PafMidiaCadastroTests.EnviarBytesAsync(troca, PafMidiaCadastroTests.Bytes(40), "image/webp")).ShouldBe(HttpStatusCode.OK);
        var trocada = await PafMidiaCadastroTests.MidiaOkAsync(root, HttpMethod.Put, $"/midias/{midia.Id}/arquivo", unidade,
            new TrocarArquivoMidiaCommand(midia.Id, troca.EnvioId, midia.Versao));

        (await ConfiguracaoDaEstacaoAsync(unidade, id)).ImagemFundoPainel.ShouldBeNull("the stored sample describes the old file");

        var obsoleta = await RecusaAsync(root, unidade, HttpMethod.Put, $"/estacoes/{id}", Atualizar(id, fila, bloco));
        obsoleta.ShouldContain("Painel.ImagemFundo: a mídia mudou desde a medição");

        // The new file is dark: the black texts already stored are refused against it.
        var reavaliada = await RecusaAsync(root, unidade, HttpMethod.Put, $"/estacoes/{id}",
            Atualizar(id, fila, bloco with { ImagemFundo = new ImagemFundoDto(midia.Id, trocada.Versao, AmostraEscura) }));
        reavaliada.ShouldContain("Painel.CorFonteFilas sobre a imagem de fundo");

        await PafApi.OkAsync<EstacaoAlteradaDto>(root, HttpMethod.Put, $"/estacoes/{id}", unidade,
            Atualizar(id, fila, PainelCom(texto: "#FFFFFF", fundo: "#000000", new ImagemFundoDto(midia.Id, trocada.Versao, AmostraEscura))));
        var depois = (await ConfiguracaoDaEstacaoAsync(unidade, id)).ImagemFundoPainel.ShouldNotBeNull();
        depois.Url.ShouldNotBe(antes.Url);
        (await BaixarAsync(depois.Url)).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OutraUnidade_Video_Inativa_SemCores_AmostraInvalida_400_ERemoverTiraAImagem()
    {
        using var root = await _api.RootAsync();
        var (unidade, fila) = await UnidadeComFilaAsync(root);
        var (outraUnidade, _) = await UnidadeComFilaAsync(root);
        var daOutra = await ImagemAsync(root, outraUnidade, "fundo.png", "image/png");
        var video = await ImagemAsync(root, unidade, "abertura.mp4", "video/mp4");
        var inativa = await ImagemAsync(root, unidade, "velha.gif", "image/gif");
        inativa = await PafMidiaCadastroTests.MidiaOkAsync(root, HttpMethod.Post, $"/midias/{inativa.Id}/disable", unidade, null);
        var boa = await ImagemAsync(root, unidade, "fundo.png", "image/png");

        (await RecusaAsync(root, unidade, HttpMethod.Post, "/estacoes",
                Painel(fila, PainelCom("#000000", "#FFFFFF", new ImagemFundoDto(daOutra.Id, daOutra.Versao, AmostraClara)))))
            .ShouldContain("Painel.ImagemFundo: mídia não encontrada nesta unidade.");
        (await RecusaAsync(root, unidade, HttpMethod.Post, "/estacoes",
                Painel(fila, PainelCom("#000000", "#FFFFFF", new ImagemFundoDto(video.Id, video.Versao, AmostraClara)))))
            .ShouldContain("Painel.ImagemFundo: a mídia precisa ser uma imagem (.jpg, .jpeg, .png, .webp, .gif).");
        (await RecusaAsync(root, unidade, HttpMethod.Post, "/estacoes",
                Painel(fila, PainelCom("#000000", "#FFFFFF", new ImagemFundoDto(inativa.Id, inativa.Versao, AmostraClara)))))
            .ShouldContain("Painel.ImagemFundo: a mídia está inativa ou sem arquivo confirmado.");
        (await RecusaAsync(root, unidade, HttpMethod.Post, "/estacoes",
                Painel(fila, new BlocoPainelDto(null, null, null, null, null, null, null, null) { ImagemFundo = new ImagemFundoDto(boa.Id, boa.Versao, AmostraClara) })))
            .ShouldContain("Painel: com imagem de fundo, informe as cores do bloco");
        (await RecusaAsync(root, unidade, HttpMethod.Post, "/estacoes",
                Painel(fila, PainelCom("#000000", "#FFFFFF", new ImagemFundoDto(boa.Id, boa.Versao, new string('0', 64))))))
            .ShouldContain("Painel.ImagemFundo.Luminancias deve ter 64 dígitos hexadecimais com ao menos uma faixa.");

        var id = await PafApi.OkAsync<Guid>(root, HttpMethod.Post, "/estacoes", unidade,
            Painel(fila, PainelCom("#000000", "#FFFFFF", new ImagemFundoDto(boa.Id, boa.Versao, AmostraClara))));
        (await ConfiguracaoDaEstacaoAsync(unidade, id)).ImagemFundoPainel.ShouldNotBeNull();

        // Disabling the mídia later takes it off the screens (M04 signs nothing for an inactive mídia).
        var desativada = await PafMidiaCadastroTests.MidiaOkAsync(root, HttpMethod.Post, $"/midias/{boa.Id}/disable", unidade, null);
        (await ConfiguracaoDaEstacaoAsync(unidade, id)).ImagemFundoPainel.ShouldBeNull();
        await PafMidiaCadastroTests.MidiaOkAsync(root, HttpMethod.Post, $"/midias/{boa.Id}/enable", unidade, null);
        (await ConfiguracaoDaEstacaoAsync(unidade, id)).ImagemFundoPainel.ShouldNotBeNull("enable changes the Versao, never the file");
        desativada.Versao.ShouldBeGreaterThan(boa.Versao);

        await PafApi.OkAsync<EstacaoAlteradaDto>(root, HttpMethod.Put, $"/estacoes/{id}", unidade,
            Atualizar(id, fila, PainelCom("#000000", "#FFFFFF", imagem: null)));
        (await ConfiguracaoDaEstacaoAsync(unidade, id)).ImagemFundoPainel.ShouldBeNull();
        var semImagem = (await PafApi.OkAsync<List<EstacaoDto>>(root, HttpMethod.Get, "/estacoes", unidade, null)).Single(e => e.Id == id);
        semImagem.Configuracao!.Painel!.ImagemFundo.ShouldBeNull();
    }

    // ─── auxiliares ──────────────────────────────────────────────────

    private static BlocoPainelDto PainelCom(string texto, string fundo, ImagemFundoDto? imagem) =>
        new(null, null, null, fundo, texto, texto, texto, null) { ImagemFundo = imagem };

    private static CreateEstacaoCommand Painel(Guid fila, BlocoPainelDto bloco) =>
        new("Painel", "Painel", [new EstacaoFilaItem("Painel", fila, 0)], [], null, new BlocosEstacaoDto(null, null, bloco));

    private static CreateEstacaoCommand Totem(Guid fila, BlocoEmissaoDto bloco) =>
        new("Totem", "Emissao", [new EstacaoFilaItem("Emissao", fila, 0)], [], null, new BlocosEstacaoDto(bloco, null, null));

    private static UpdateEstacaoCommand Atualizar(Guid id, Guid fila, BlocoPainelDto bloco) =>
        new(id, "Painel", [new EstacaoFilaItem("Painel", fila, 0)], [], null, new BlocosEstacaoDto(null, null, bloco));

    private async Task<(Guid Unidade, Guid Fila)> UnidadeComFilaAsync(HttpClient root)
    {
        var unidade = await _api.CriarUnidadeAsync(root);
        var prioridade = await PafApi.CriarPrioridadeAsync(root, unidade, "Normal");
        return (unidade, await PafApi.CriarFilaAsync(root, unidade, "Caixa", prioridade, "A"));
    }

    private static async Task<MidiaDto> ImagemAsync(HttpClient root, Guid unidade, string nome, string mime)
    {
        var envio = await PafMidiaCadastroTests.PedirEnvioAsync(root, unidade, nome, mime);
        (await PafMidiaCadastroTests.EnviarBytesAsync(envio, PafMidiaCadastroTests.Bytes(32), mime)).ShouldBe(HttpStatusCode.OK);
        return await PafMidiaCadastroTests.MidiaOkAsync(root, HttpMethod.Post, "/midias", unidade, new CreateMidiaCommand(envio.EnvioId, nome, 30));
    }

    private static async Task<string> RecusaAsync(HttpClient client, Guid unidade, HttpMethod metodo, string rota, object corpo)
    {
        using var resposta = await PafApi.EnviarAsync(client, metodo, rota, unidade, corpo);
        var texto = await resposta.Content.ReadAsStringAsync();
        resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest, texto);
        // The body escapes non-ASCII characters; compare against the decoded strings of the problem details.
        using var problema = JsonDocument.Parse(texto);
        return string.Join('\n', Textos(problema.RootElement));
    }

    /// <summary>What the station's own screens read (the credential sets the same tenant and unidade).</summary>
    private async Task<ConfiguracaoEstacaoDto> ConfiguracaoDaEstacaoAsync(Guid unidade, Guid estacaoId)
    {
        using var scope = factory.Services.CreateScope();
        var raiz = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(raiz!);
        scope.ServiceProvider.GetRequiredService<IUnidadeCorrente>().Definir(unidade);
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new ObterConfiguracaoEstacaoQuery(estacaoId));
    }

    private static IEnumerable<string> Textos(JsonElement elemento) => elemento.ValueKind switch
    {
        JsonValueKind.String => [elemento.GetString()!],
        JsonValueKind.Array => elemento.EnumerateArray().SelectMany(Textos),
        JsonValueKind.Object => elemento.EnumerateObject().SelectMany(p => Textos(p.Value)),
        _ => [],
    };

    private static async Task<HttpStatusCode> BaixarAsync(Uri url)
    {
        using var http = new HttpClient { Timeout = PafMidiaCadastroTests.Prazo };
        using var resposta = await http.GetAsync(url);
        return resposta.StatusCode;
    }
}
