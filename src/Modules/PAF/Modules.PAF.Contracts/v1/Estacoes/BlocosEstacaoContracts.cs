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
    int? PrazoCicloEmissaoSegundos);

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
    string? Modo);
