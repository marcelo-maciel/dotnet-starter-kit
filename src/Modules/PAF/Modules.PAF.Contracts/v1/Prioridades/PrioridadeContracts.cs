using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Prioridades;

public sealed record PrioridadeDto(Guid Id, Guid UnidadeId, string Nome, int Valor, bool Ativa);

public sealed record CreatePrioridadeCommand(string Nome, int Valor) : ICommand<Guid>;

public sealed record UpdatePrioridadeCommand(Guid Id, string Nome, int Valor) : ICommand<Guid>;

public sealed record EnablePrioridadeCommand(Guid Id) : ICommand<Guid>;

public sealed record DisablePrioridadeCommand(Guid Id) : ICommand<Guid>;

public sealed record ListPrioridadesQuery : IQuery<IReadOnlyList<PrioridadeDto>>;
