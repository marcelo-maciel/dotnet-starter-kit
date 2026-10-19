using FSH.Modules.PAF.Contracts.v1.Filas;
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
    IReadOnlyList<EstacaoPrioridadeItem> Prioridades,
    string? Ramal,
    BlocosEstacaoDto? Configuracao);

/// <summary>
/// <paramref name="Tipo"/> is one of the seven RF04 types (Administracao, Emissao, EmissaoGuiche,
/// EmissaoPainel, Guiche, GuichePainel, Painel) and is fixed after creation.
/// </summary>
public sealed record CreateEstacaoCommand(
    string Nome,
    string Tipo,
    IReadOnlyList<EstacaoFilaItem> Filas,
    IReadOnlyList<EstacaoPrioridadeItem> Prioridades,
    string? Ramal,
    BlocosEstacaoDto Configuracao) : ICommand<Guid>, IEscritaDeCadastro;

/// <summary>Replaces the station's filas and prioridades lists, its ramal and its blocks; the type stays (D25).</summary>
public sealed record UpdateEstacaoCommand(
    Guid Id,
    string Nome,
    IReadOnlyList<EstacaoFilaItem> Filas,
    IReadOnlyList<EstacaoPrioridadeItem> Prioridades,
    string? Ramal,
    BlocosEstacaoDto Configuracao) : ICommand<EstacaoAlteradaDto>, IEscritaDeCadastro;

/// <summary>
/// <paramref name="CombinacoesSemCobertura"/>: combinations this write left without any guichê (RF02, RF04). A
/// warning, never a refusal: they leave the emission screens, and the fichas already waiting stay callable.
/// </summary>
public sealed record EstacaoAlteradaDto(Guid Id, IReadOnlyList<CombinacaoFilaPrioridadeDto> CombinacoesSemCobertura);

public sealed record EnableEstacaoCommand(Guid Id) : ICommand<Guid>, IEscritaDeCadastro;

/// <summary>
/// Deactivates the station (RF04): revokes its device links, releases its guichê when nothing is in progress on it and,
/// when <see cref="CancelarAtendimentoId"/> names the atendimento in progress there, cancels it by RF12 in the same act.
/// </summary>
public sealed record DisableEstacaoCommand(Guid Id) : ICommand<EstacaoDesativadaDto>, IEscritaDeCadastro
{
    /// <summary>
    /// The in-progress atendimento the deactivation dialog showed; a station holds at most one
    /// (UX_Atendimentos_EstacaoEmCurso). Null leaves it for the attendant to finish, which then releases the guichê.
    /// </summary>
    public Guid? CancelarAtendimentoId { get; init; }
}

public sealed record ListEstacoesQuery : IQuery<IReadOnlyList<EstacaoDto>>;
