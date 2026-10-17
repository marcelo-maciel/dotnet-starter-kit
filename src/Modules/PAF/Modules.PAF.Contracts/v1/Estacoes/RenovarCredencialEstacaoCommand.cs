using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Estacoes;

/// <summary>Emite um token de acesso novo pelo segredo da credencial, enquanto o vínculo e a estação valem.</summary>
public sealed record RenovarCredencialEstacaoCommand(string Segredo) : ICommand<AcessoEstacaoDto>;

public sealed record AcessoEstacaoDto(string Papel, string AccessToken, DateTime AccessTokenExpiresAt);
