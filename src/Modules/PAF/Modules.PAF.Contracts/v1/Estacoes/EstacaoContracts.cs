using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Estacoes;

/// <summary><paramref name="Papel"/> is one of Emissao, Guiche, Painel.</summary>
public sealed record EstacaoFilaItem(string Papel, Guid FilaId, int Ordem);

public sealed record EstacaoPrioridadeItem(Guid PrioridadeId, int Ordem);

public sealed record EstacaoDto(
    Guid Id,
    Guid UnidadeId,
    string Nome,
    string Tipo,
    bool Ativa,
    IReadOnlyList<EstacaoFilaItem> Filas,
    IReadOnlyList<EstacaoPrioridadeItem> Prioridades);

/// <summary>
/// <paramref name="Tipo"/> is one of the seven RF04 types (Administracao, Emissao, EmissaoGuiche,
/// EmissaoPainel, Guiche, GuichePainel, Painel) and is fixed after creation.
/// </summary>
public sealed record CreateEstacaoCommand(
    string Nome,
    string Tipo,
    IReadOnlyList<EstacaoFilaItem> Filas,
    IReadOnlyList<EstacaoPrioridadeItem> Prioridades) : ICommand<Guid>;

/// <summary>Replaces the station's filas and prioridades lists.</summary>
public sealed record UpdateEstacaoCommand(
    Guid Id,
    string Nome,
    IReadOnlyList<EstacaoFilaItem> Filas,
    IReadOnlyList<EstacaoPrioridadeItem> Prioridades) : ICommand<Guid>;

public sealed record EnableEstacaoCommand(Guid Id) : ICommand<Guid>;

public sealed record DisableEstacaoCommand(Guid Id) : ICommand<Guid>;

public sealed record ListEstacoesQuery : IQuery<IReadOnlyList<EstacaoDto>>;
