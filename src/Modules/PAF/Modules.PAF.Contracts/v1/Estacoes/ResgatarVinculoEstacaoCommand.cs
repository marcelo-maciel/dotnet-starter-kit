using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Estacoes;

/// <summary>Troca o link de uso único pelas credenciais da estação, uma por papel (emissão, painel).</summary>
public sealed record ResgatarVinculoEstacaoCommand(string Token) : ICommand<CredenciaisEstacaoDto>;

public sealed record CredenciaisEstacaoDto(IReadOnlyList<CredencialEstacaoDto> Credenciais);

/// <summary>
/// <see cref="Segredo"/> é a credencial de longa duração, usada só para renovar; <see cref="AccessToken"/> é o
/// token curto que vai em cada requisição. O segredo só existe nesta resposta: o servidor guarda o hash.
/// </summary>
public sealed record CredencialEstacaoDto(string Papel, string Segredo, string AccessToken, DateTime AccessTokenExpiresAt);
