using System.Text.Json.Serialization;

namespace FSH.Modules.PAF.Contracts.v1.Estacoes;

// Unmapped members are refused (400) on every block: a misspelled key would otherwise be dropped in silence and the
// screen would keep its default without anyone noticing (RF04).

/// <summary>
/// The RF04 configuration blocks. A block is present exactly when the station type has its papel (Administracao has
/// none). Colors are <c>#RRGGBB</c>; within a block they come all together or not at all, because each text color is
/// checked against the block background (contrast at least 4.5:1, RNF19). An absent value keeps the screen default.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BlocosEstacaoDto(
    BlocoEmissaoDto? Emissao,
    BlocoGuicheDto? Guiche,
    BlocoPainelDto? Painel);

/// <summary>
/// Totem block. <paramref name="TamanhoFontePx"/> has the configurable floor of D17; <paramref name="TamanhoBotaoPx"/>
/// at least 44 (RNF19). <paramref name="PrazoCicloEmissaoSegundos"/> is the totem's emission cycle deadline (F19).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BlocoEmissaoDto(
    string? Titulo,
    int? TamanhoFontePx,
    int? TamanhoBotaoPx,
    string? CorFundoBotao,
    string? CorFonteBotao,
    string? CorDescricaoBotao,
    int? PrazoCicloEmissaoSegundos)
{
    /// <summary>C17: color of the totem texts drawn straight on the screen background (title, messages); null = black.</summary>
    public string? CorTexto { get; init; }

    /// <summary>C17: background image of the totem; null = none.</summary>
    public ImagemFundoDto? ImagemFundo { get; init; }
}

/// <summary>
/// Guichê block. <paramref name="Numero"/> is what the painel announces; <paramref name="LadoSeta"/> is Esquerda or
/// Direita. <paramref name="SelecionarFicha"/> is the RF04 option "selecionar ficha para atendimento".
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BlocoGuicheDto(
    int Numero,
    string? NomeExibicao,
    string? LadoSeta,
    bool RespeitarOrdemFilas,
    bool SelecionarFicha);

/// <summary>Painel block. <paramref name="Modo"/> is Normal or Informativo.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BlocoPainelDto(
    int? TempoExibicaoChamadaSegundos,
    bool? MostrarDataHora,
    int? TamanhoFontePx,
    string? CorFundo,
    string? CorFonteFilas,
    string? CorFonteChamada,
    string? CorFonteAlternada,
    string? Modo)
{
    /// <summary>C17: background image of the painel; null = none. Requires the four colors of the block.</summary>
    public ImagemFundoDto? ImagemFundo { get; init; }
}

/// <summary>
/// C17 (RF04, REQUISITOS 8.1 l.990, D16): an image mídia of the current unidade as a block background.
/// <paramref name="VersaoMidia"/> is the mídia <c>Versao</c> the sample was measured on (a file swap raises it, so a stale
/// sample is refused). <paramref name="Luminancias"/> is the luminance sample the cadastro measured in the browser: 64 hex
/// digits = 256 bits, bit i (most significant bit of each digit first) set when some sampled point of the image, composed
/// over the block background (totem white, painel <c>CorFundo</c>), has WCAG relative luminance in [i/256, (i+1)/256].
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ImagemFundoDto(Guid MidiaId, int VersaoMidia, string Luminancias);
