using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Atendimentos;

/// <summary>What the forwarding screen offers for the atendimento started on the session's guichê (RF17).</summary>
public sealed record ListDestinosEncaminhamentoQuery : IQuery<DestinosEncaminhamentoDto>;

/// <summary>
/// Origin of the forwarding and its candidate destinations. <paramref name="PrioridadeOriginalId"/> lets the
/// screen pre-select the original prioridade when it is in the intersection of the marked filas.
/// </summary>
public sealed record DestinosEncaminhamentoDto(
    Guid AtendimentoId,
    string Senha,
    Guid FilaOrigemId,
    string FilaOrigem,
    Guid PrioridadeOriginalId,
    IReadOnlyList<DestinoEncaminhamentoDto> Destinos);

/// <summary>
/// An active fila of the unit other than the origin. <paramref name="ForaDoHorario"/> marks a closed window without
/// blocking it (RF17). <paramref name="Prioridades"/> are the active ones associated to the fila AND covered by an active
/// guichê; the screen intersects them over the marked filas.
/// </summary>
public sealed record DestinoEncaminhamentoDto(Guid FilaId, string Nome, bool ForaDoHorario, IReadOnlyList<PrioridadeDestinoDto> Prioridades);

public sealed record PrioridadeDestinoDto(Guid PrioridadeId, string Nome);

/// <summary>Body of the forwarding: the destination filas and the prioridade the ficha enters them with.</summary>
public sealed record EncaminharAtendimentoRequest(IReadOnlyList<Guid> FilaIds, Guid PrioridadeId);

/// <summary>Closes the started atendimento and opens one atendimento per destination fila on the same ficha (RF17).</summary>
public sealed record EncaminharAtendimentoCommand(Guid AtendimentoId, IReadOnlyList<Guid> FilaIds, Guid PrioridadeId)
    : ICommand<AtendimentoEncaminhadoDto>, IEscritaDeCadastro;

/// <summary><paramref name="Versao"/> is the closed origin's; <paramref name="Destinos"/> the atendimentos created, in request order.</summary>
public sealed record AtendimentoEncaminhadoDto(Guid AtendimentoId, int Versao, IReadOnlyList<AtendimentoDestinoDto> Destinos);

public sealed record AtendimentoDestinoDto(Guid AtendimentoId, Guid FilaId);
