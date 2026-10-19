using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Contratos;

/// <summary><c>Politica</c> is Recusar or UltrapassarComAviso.</summary>
public sealed record ContratoUnidadeDto(
    Guid Id,
    Guid UnidadeId,
    DateOnly InicioVigencia,
    DateOnly? FimVigencia,
    string Plano,
    int LimiteSessoes,
    string Politica);

/// <summary>
/// Written by the Codefi operator, who has no current unidade: the unidade comes in the command and is
/// checked under the client's tenant filter (arquitetura seção 6).
/// </summary>
public sealed record CreateContratoUnidadeCommand(
    Guid UnidadeId,
    DateOnly InicioVigencia,
    DateOnly? FimVigencia,
    string Plano,
    int LimiteSessoes,
    string Politica) : ICommand<Guid>, IEscritaDeCadastro;

public sealed record UpdateContratoUnidadeCommand(
    Guid Id,
    DateOnly InicioVigencia,
    DateOnly? FimVigencia,
    string Plano,
    int LimiteSessoes,
    string Politica) : ICommand<Guid>, IEscritaDeCadastro;

/// <summary>Contracts of the current unidade: the client sees its limit, read-only (RF23).</summary>
public sealed record ListContratosUnidadeQuery : IQuery<IReadOnlyList<ContratoUnidadeDto>>;
