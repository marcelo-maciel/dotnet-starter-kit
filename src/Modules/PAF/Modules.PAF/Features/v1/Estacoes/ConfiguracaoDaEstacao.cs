using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Aparencia;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Domain;

namespace FSH.Modules.PAF.Features.v1.Estacoes;

/// <summary>
/// The RF04 configuration blocks of a station: validation by the type's papéis, and the stored <c>jsonb</c> shape.
/// The operational keys already read by the guichê call and by the totem and painel screens
/// (<c>respeitarOrdemFilas</c>, <c>selecionarFicha</c>, <c>tempoExibicaoChamadaSegundos</c>,
/// <c>prazoCicloEmissaoSegundos</c>) stay at the root of the document; the appearance and the guichê identity live in
/// the <c>emissao</c>, <c>guiche</c> and <c>painel</c> objects.
/// </summary>
internal static class ConfiguracaoDaEstacao
{
    public const int TempoMinimoSegundos = 1;
    public const int TempoMaximoSegundos = 600;
    public const int AlvoMinimoBotaoPx = 44;
    public const int TamanhoMaximoPx = 400;
    public const int TamanhoMinimoFontePainelPx = 8;
    public const int NumeroGuicheMaximo = 9999;
    public const int TituloMaxLength = 120;
    public const int NomeExibicaoMaxLength = 60;

    /// <summary>C17: what the totem screen draws today (black on white) when the block sets no text color.</summary>
    public const string CorTextoPadraoTotem = "#000000";
    public const string FundoPadraoTotem = "#FFFFFF";

    private static readonly string[] LadosSeta = ["Esquerda", "Direita"];
    private static readonly string[] ModosPainel = ["Normal", "Informativo"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Throws 400 with every broken rule; <paramref name="fonteMinimaTotemPx"/> is the D17 parameter.</summary>
    public static void Validar(TipoEstacao tipo, BlocosEstacaoDto configuracao, int fonteMinimaTotemPx)
    {
        ArgumentNullException.ThrowIfNull(configuracao);
        var papeis = ListasDaEstacao.Papeis(tipo);
        var erros = new List<string>();

        ExigirBlocoDoPapel(erros, tipo, papeis, PapelEstacao.Emissao, configuracao.Emissao is not null);
        ExigirBlocoDoPapel(erros, tipo, papeis, PapelEstacao.Guiche, configuracao.Guiche is not null);
        ExigirBlocoDoPapel(erros, tipo, papeis, PapelEstacao.Painel, configuracao.Painel is not null);

        if (configuracao.Emissao is { } emissao)
        {
            ValidarEmissao(erros, emissao, fonteMinimaTotemPx);
        }

        if (configuracao.Guiche is { } guiche)
        {
            ValidarGuiche(erros, guiche);
        }

        if (configuracao.Painel is { } painel)
        {
            ValidarPainel(erros, painel);
        }

        if (erros.Count > 0)
        {
            throw new CustomException("Configuração da estação inválida.", erros, HttpStatusCode.BadRequest);
        }
    }

    /// <summary>
    /// <paramref name="enviosDasImagens"/>: current <c>EnvioId</c> of each background mídia, from
    /// <see cref="ImagemDeFundoDaEstacao.ConferirAsync"/>; stored so a later file swap is detected on read.
    /// </summary>
    public static string Serializar(BlocosEstacaoDto configuracao, IReadOnlyDictionary<Guid, Guid> enviosDasImagens)
    {
        ArgumentNullException.ThrowIfNull(configuracao);
        ArgumentNullException.ThrowIfNull(enviosDasImagens);
        var e = configuracao.Emissao;
        var g = configuracao.Guiche;
        var p = configuracao.Painel;
        var documento = new Documento(
            RespeitarOrdemFilas: g?.RespeitarOrdemFilas,
            SelecionarFicha: g?.SelecionarFicha,
            TempoExibicaoChamadaSegundos: p?.TempoExibicaoChamadaSegundos,
            PrazoCicloEmissaoSegundos: e?.PrazoCicloEmissaoSegundos,
            Emissao: e is null ? null : new AparenciaEmissao(
                e.Titulo, e.TamanhoFontePx, e.TamanhoBotaoPx, e.CorFundoBotao, e.CorFonteBotao, e.CorDescricaoBotao,
                e.CorTexto, Gravar(e.ImagemFundo, enviosDasImagens)),
            Guiche: g is null ? null : new IdentidadeGuiche(g.Numero, g.NomeExibicao, g.LadoSeta),
            Painel: p is null ? null : new AparenciaPainel(
                p.MostrarDataHora, p.TamanhoFontePx, p.CorFundo, p.CorFonteFilas, p.CorFonteChamada, p.CorFonteAlternada, p.Modo,
                Gravar(p.ImagemFundo, enviosDasImagens)));
        return JsonSerializer.Serialize(documento, Json);
    }

    /// <summary>C17: the stored background images of both blocks (null when absent), for the screens' read URL.</summary>
    public static (ImagemFundoGravada? Emissao, ImagemFundoGravada? Painel) ImagensDeFundo(string? configuracao)
    {
        var d = LerDocumento(configuracao);
        return (d?.Emissao?.ImagemFundo, d?.Painel?.ImagemFundo);
    }

    private static ImagemFundoGravada? Gravar(ImagemFundoDto? imagem, IReadOnlyDictionary<Guid, Guid> enviosDasImagens) =>
        imagem is null ? null : new ImagemFundoGravada(imagem.MidiaId, imagem.VersaoMidia, enviosDasImagens[imagem.MidiaId], imagem.Luminancias);

    /// <summary>The blocks as stored; null when nothing was ever written through the cadastro.</summary>
    public static BlocosEstacaoDto? Ler(string? configuracao)
    {
        var d = LerDocumento(configuracao);
        if (d is null)
        {
            return null;
        }

        return new BlocosEstacaoDto(
            d.Emissao is not { } e ? null : new BlocoEmissaoDto(
                e.Titulo, e.TamanhoFontePx, e.TamanhoBotaoPx, e.CorFundoBotao, e.CorFonteBotao, e.CorDescricaoBotao,
                d.PrazoCicloEmissaoSegundos)
            {
                CorTexto = e.CorTexto,
                ImagemFundo = Expor(e.ImagemFundo),
            },
            d.Guiche is not { } g ? null : new BlocoGuicheDto(
                g.Numero, g.NomeExibicao, g.LadoSeta, d.RespeitarOrdemFilas ?? false, d.SelecionarFicha ?? false),
            d.Painel is not { } p ? null : new BlocoPainelDto(
                d.TempoExibicaoChamadaSegundos, p.MostrarDataHora, p.TamanhoFontePx, p.CorFundo, p.CorFonteFilas,
                p.CorFonteChamada, p.CorFonteAlternada, p.Modo)
            {
                ImagemFundo = Expor(p.ImagemFundo),
            });
    }

    private static ImagemFundoDto? Expor(ImagemFundoGravada? imagem) =>
        imagem is null ? null : new ImagemFundoDto(imagem.MidiaId, imagem.VersaoMidia, imagem.Luminancias);

    /// <summary>Number and display name the painel announces for a guichê; both null when the block was never written.</summary>
    public static (int? Numero, string? NomeExibicao) IdentidadeDoGuiche(string? configuracao)
    {
        var g = LerDocumento(configuracao)?.Guiche;
        return (g?.Numero, g?.NomeExibicao);
    }

    private static Documento? LerDocumento(string? configuracao)
    {
        if (string.IsNullOrWhiteSpace(configuracao))
        {
            return null;
        }

        // Rows written before this cadastro (or by tests) may hold any shape; the announcement must not fail on them.
        try
        {
            return JsonSerializer.Deserialize<Documento>(configuracao, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ExigirBlocoDoPapel(List<string> erros, TipoEstacao tipo, IReadOnlyList<PapelEstacao> papeis, PapelEstacao papel, bool presente)
    {
        var doTipo = papeis.Contains(papel);
        if (doTipo && !presente)
        {
            erros.Add($"O tipo {tipo} exige o bloco {papel}.");
        }
        else if (!doTipo && presente)
        {
            erros.Add($"O tipo {tipo} não tem o bloco {papel}.");
        }
    }

    private static void ValidarEmissao(List<string> erros, BlocoEmissaoDto e, int fonteMinimaTotemPx)
    {
        if (e.Titulo is { Length: > TituloMaxLength })
        {
            erros.Add($"Emissao.Titulo passa de {TituloMaxLength} caracteres.");
        }

        if (e.TamanhoFontePx is { } fonte && (fonte < fonteMinimaTotemPx || fonte > TamanhoMaximoPx))
        {
            erros.Add($"Emissao.TamanhoFontePx deve estar entre {fonteMinimaTotemPx} e {TamanhoMaximoPx}.");
        }

        if (e.TamanhoBotaoPx is { } botao && (botao < AlvoMinimoBotaoPx || botao > TamanhoMaximoPx))
        {
            erros.Add($"Emissao.TamanhoBotaoPx deve estar entre {AlvoMinimoBotaoPx} e {TamanhoMaximoPx}.");
        }

        ValidarTempo(erros, "Emissao.PrazoCicloEmissaoSegundos", e.PrazoCicloEmissaoSegundos);
        ValidarCores(erros, "Emissao", ("CorFundoBotao", e.CorFundoBotao),
            [("CorFonteBotao", e.CorFonteBotao), ("CorDescricaoBotao", e.CorDescricaoBotao)]);
        ValidarTextoDoTotem(erros, e);
    }

    // C17: the buttons keep their own solid color, so on the totem only the title and the messages sit on the screen
    // background: the image when there is one, else the screen's white.
    private static void ValidarTextoDoTotem(List<string> erros, BlocoEmissaoDto e)
    {
        if (e.CorTexto is not null && !Contraste.CorValida(e.CorTexto))
        {
            erros.Add("Emissao.CorTexto deve estar no formato #RRGGBB.");
            return;
        }

        var texto = e.CorTexto ?? CorTextoPadraoTotem;
        if (e.ImagemFundo is { } imagem)
        {
            ValidarContraImagem(erros, "Emissao", imagem, [("CorTexto", texto)]);
        }
        else if (e.CorTexto is not null && !Contraste.Suficiente(texto, FundoPadraoTotem))
        {
            erros.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Emissao.CorTexto sobre o fundo branco do totem tem contraste {Math.Floor(Contraste.Razao(texto, FundoPadraoTotem) * 100) / 100:0.00}:1, abaixo do mínimo de {Contraste.RazaoMinima}:1."));
        }
    }

    // C17: every painel text sits on the background, so with an image each text color is measured against it. Without
    // the block colors the screen would use its default translucent whites, which nobody measured: required.
    private static void ValidarImagemDoPainel(List<string> erros, BlocoPainelDto p)
    {
        if (p.ImagemFundo is not { } imagem)
        {
            return;
        }

        if (p.CorFundo is null)
        {
            erros.Add("Painel: com imagem de fundo, informe as cores do bloco (CorFundo, CorFonteFilas, CorFonteChamada, CorFonteAlternada).");
            return;
        }

        (string Campo, string? Cor)[] textos =
            [("CorFonteFilas", p.CorFonteFilas), ("CorFonteChamada", p.CorFonteChamada), ("CorFonteAlternada", p.CorFonteAlternada)];
        if (textos.All(t => Contraste.CorValida(t.Cor)))
        {
            ValidarContraImagem(erros, "Painel", imagem, [.. textos.Select(t => (t.Campo, t.Cor!))]);
        }
    }

    private static void ValidarContraImagem(List<string> erros, string bloco, ImagemFundoDto imagem, (string Campo, string Cor)[] textos)
    {
        if (!ContrasteImagem.AmostraValida(imagem.Luminancias))
        {
            erros.Add($"{bloco}.ImagemFundo.Luminancias deve ter {ContrasteImagem.DigitosDaAmostra} dígitos hexadecimais com ao menos uma faixa.");
            return;
        }

        foreach (var (campo, cor) in textos)
        {
            var razao = ContrasteImagem.PiorRazao(cor, imagem.Luminancias);
            if (razao < Contraste.RazaoMinima)
            {
                erros.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{bloco}.{campo} sobre a imagem de fundo tem contraste {Math.Floor(razao * 100) / 100:0.00}:1 no pior ponto, abaixo do mínimo de {Contraste.RazaoMinima}:1."));
            }
        }
    }

    private static void ValidarGuiche(List<string> erros, BlocoGuicheDto g)
    {
        if (g.Numero is < 1 or > NumeroGuicheMaximo)
        {
            erros.Add($"Guiche.Numero deve estar entre 1 e {NumeroGuicheMaximo}.");
        }

        if (g.NomeExibicao is { Length: > NomeExibicaoMaxLength })
        {
            erros.Add($"Guiche.NomeExibicao passa de {NomeExibicaoMaxLength} caracteres.");
        }

        if (g.LadoSeta is not null && !LadosSeta.Contains(g.LadoSeta, StringComparer.Ordinal))
        {
            erros.Add("Guiche.LadoSeta deve ser Esquerda ou Direita.");
        }
    }

    private static void ValidarPainel(List<string> erros, BlocoPainelDto p)
    {
        ValidarTempo(erros, "Painel.TempoExibicaoChamadaSegundos", p.TempoExibicaoChamadaSegundos);

        if (p.TamanhoFontePx is { } fonte && (fonte < TamanhoMinimoFontePainelPx || fonte > TamanhoMaximoPx))
        {
            erros.Add($"Painel.TamanhoFontePx deve estar entre {TamanhoMinimoFontePainelPx} e {TamanhoMaximoPx}.");
        }

        if (p.Modo is not null && !ModosPainel.Contains(p.Modo, StringComparer.Ordinal))
        {
            erros.Add("Painel.Modo deve ser Normal ou Informativo.");
        }

        ValidarCores(erros, "Painel", ("CorFundo", p.CorFundo),
            [("CorFonteFilas", p.CorFonteFilas), ("CorFonteChamada", p.CorFonteChamada), ("CorFonteAlternada", p.CorFonteAlternada)]);
        ValidarImagemDoPainel(erros, p);
    }

    private static void ValidarTempo(List<string> erros, string campo, int? segundos)
    {
        if (segundos is { } s && (s < TempoMinimoSegundos || s > TempoMaximoSegundos))
        {
            erros.Add($"{campo} deve estar entre {TempoMinimoSegundos} e {TempoMaximoSegundos} segundos.");
        }
    }

    // Each text color is judged against the block background, so a block with only some of its colors set could pair a
    // chosen text with the screen's default background, which the cadastro never saw: all of them or none.
    private static void ValidarCores(List<string> erros, string bloco, (string Campo, string? Cor) fundo, (string Campo, string? Cor)[] textos)
    {
        (string Campo, string? Cor)[] todas = [fundo, .. textos];
        if (todas.All(c => c.Cor is null))
        {
            return;
        }

        if (todas.Any(c => c.Cor is null))
        {
            erros.Add($"{bloco}: informe todas as cores do bloco ({string.Join(", ", todas.Select(c => c.Campo))}) ou nenhuma.");
            return;
        }

        var invalidas = todas.Where(c => !Contraste.CorValida(c.Cor)).ToList();
        erros.AddRange(invalidas.Select(c => $"{bloco}.{c.Campo} deve estar no formato #RRGGBB."));
        if (invalidas.Count > 0)
        {
            return;
        }

        foreach (var (campo, cor) in textos)
        {
            var razao = Contraste.Razao(cor!, fundo.Cor!);
            if (razao < Contraste.RazaoMinima)
            {
                erros.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{bloco}.{campo} sobre {fundo.Campo} tem contraste {Math.Floor(razao * 100) / 100:0.00}:1, abaixo do mínimo de {Contraste.RazaoMinima}:1."));
            }
        }
    }

    private sealed record Documento(
        bool? RespeitarOrdemFilas,
        bool? SelecionarFicha,
        int? TempoExibicaoChamadaSegundos,
        int? PrazoCicloEmissaoSegundos,
        AparenciaEmissao? Emissao,
        IdentidadeGuiche? Guiche,
        AparenciaPainel? Painel);

    private sealed record AparenciaEmissao(
        string? Titulo, int? TamanhoFontePx, int? TamanhoBotaoPx, string? CorFundoBotao, string? CorFonteBotao, string? CorDescricaoBotao,
        string? CorTexto, ImagemFundoGravada? ImagemFundo);

    private sealed record IdentidadeGuiche(int Numero, string? NomeExibicao, string? LadoSeta);

    private sealed record AparenciaPainel(
        bool? MostrarDataHora, int? TamanhoFontePx, string? CorFundo, string? CorFonteFilas, string? CorFonteChamada,
        string? CorFonteAlternada, string? Modo, ImagemFundoGravada? ImagemFundo);
}

/// <summary>
/// C17: a background image as stored in the station <c>jsonb</c>. <paramref name="EnvioId"/> is the mídia's file when the
/// sample was accepted: once the mídia points at another envio (file swap), the sample no longer describes what the
/// screen would show, and the screens get no URL until the cadastro measures and saves again.
/// </summary>
internal sealed record ImagemFundoGravada(Guid MidiaId, int VersaoMidia, Guid EnvioId, string Luminancias);
