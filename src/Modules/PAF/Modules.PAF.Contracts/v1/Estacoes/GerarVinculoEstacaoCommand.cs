using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Estacoes;

/// <summary>Gera o link de uso único da estação e revoga o vínculo anterior dela (RF04).</summary>
public sealed record GerarVinculoEstacaoCommand(Guid EstacaoId) : ICommand<VinculoEstacaoGeradoDto>, IEscritaDeCadastro;

/// <summary>
/// O link é montado pelo front com <see cref="Tenant"/> legível e <see cref="Token"/> opaco. O token só
/// existe nesta resposta: o servidor guarda apenas o hash.
/// </summary>
public sealed record VinculoEstacaoGeradoDto(Guid VinculoId, string Tenant, string Token);
