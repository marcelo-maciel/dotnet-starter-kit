using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Painel;

/// <summary>What the painel plays (RF21 amended on 17/10/2026, D12); the station comes from its credential.</summary>
public sealed record ObterMidiasPainelQuery(Guid EstacaoId) : IQuery<MidiasPainelDto>;

/// <summary>
/// <paramref name="Agora"/> is the database clock at São Paulo offset: the screen keeps its offset to the local clock and
/// reads the windows below on that wall clock. <paramref name="Lista"/> is the station's loop in order (only playable
/// items: inactive mídias and mídias without a confirmed file are left out). <paramref name="Programacoes"/> are today's
/// active programações of the unidade in precedence order (D13): the first one whose window contains the instant is on
/// air and covers the loop for its window. Both empty = only the calls, full screen. Re-read on
/// <c>ProgramacaoAlterada</c>, on reconnect, at the São Paulo day turn and before a URL expires.
/// </summary>
public sealed record MidiasPainelDto(
    DateTimeOffset Agora,
    IReadOnlyList<ItemMidiaPainelDto> Lista,
    IReadOnlyList<ProgramacaoPainelDto> Programacoes);

/// <summary>One loop entry; <paramref name="Tipo"/> is <c>Video</c>, <c>Imagem</c> or <c>Audio</c>.</summary>
public sealed record ItemMidiaPainelDto(
    Guid ItemId,
    Guid MidiaId,
    string Tipo,
    int DuracaoSegundos,
    Uri Url,
    DateTimeOffset ExpiraEm);

/// <summary>
/// Window [<paramref name="InicioSegundos"/>, <paramref name="FimSegundos"/>) in seconds from São Paulo midnight of
/// today, the mídia played <paramref name="Repeticoes"/> times; the end never passes 86400 (D13).
/// </summary>
public sealed record ProgramacaoPainelDto(
    Guid ProgramacaoId,
    Guid MidiaId,
    string Tipo,
    int DuracaoSegundos,
    int Repeticoes,
    int InicioSegundos,
    int FimSegundos,
    Uri Url,
    DateTimeOffset ExpiraEm);
