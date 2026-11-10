using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Estacoes;

/// <summary>Revoga o vínculo vigente sem apagar a estação; as credenciais dele caem na requisição seguinte.</summary>
public sealed record RevogarVinculoEstacaoCommand(Guid EstacaoId) : ICommand, IEscritaDeCadastro;
