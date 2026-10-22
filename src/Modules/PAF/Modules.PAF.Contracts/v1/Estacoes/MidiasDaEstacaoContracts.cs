using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Estacoes;

/// <summary>
/// One entry of a painel station's media list (RF04 painel block, RF21 amended on 17/10/2026): played in a loop by
/// <paramref name="Ordem"/>. The same mídia may appear more than once. <paramref name="Ativa"/> is the mídia's own
/// flag: an inactive entry stays in the list but the painel skips it.
/// </summary>
public sealed record ItemMidiaDaEstacaoDto(
    Guid Id,
    int Ordem,
    Guid MidiaId,
    string Descricao,
    string Tipo,
    int DuracaoSegundos,
    bool Ativa);

public sealed record MidiasDaEstacaoDto(Guid EstacaoId, IReadOnlyList<ItemMidiaDaEstacaoDto> Itens);

/// <summary>
/// Replaces the whole ordered list of the painel station <paramref name="EstacaoId"/> of the current unidade in one
/// transaction; the position in <paramref name="Midias"/> is the order. Only a station with the painel role (400), only
/// active mídias of the same unidade with the file confirmed (400). An empty list clears it.
/// </summary>
public sealed record SubstituirMidiasDaEstacaoCommand(Guid EstacaoId, IReadOnlyList<Guid> Midias)
    : ICommand<MidiasDaEstacaoDto>, IEscritaDeCadastro
{
    /// <summary>ponytail: a fixed ceiling for one painel loop; raise it if a client really plays more.</summary>
    public const int MaximoItens = 100;
}

/// <summary>The stored list of a station of the current unidade, for the cadastro screen (404 outside it).</summary>
public sealed record ObterMidiasDaEstacaoQuery(Guid EstacaoId) : IQuery<MidiasDaEstacaoDto>;
