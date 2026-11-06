using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FSH.Framework.Shared.Constants;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Domain;

namespace FSH.Modules.PAF.Estacoes;

/// <summary>
/// Assina o token de acesso da estação pelo ITokenService público do kit (ADR 0008, "Token da estação").
/// O kit só usa o subject em log; todas as claims vêm daqui. O identificador da estação ocupa o lugar do
/// usuário (sub e nameidentifier), para a idempotência da emissão do RF11 ter um chamador por estação.
/// A claim de tenant é obrigatória: na requisição autenticada é ela, e não o cabeçalho, que decide o tenant.
/// </summary>
public sealed class EmissorAcessoEstacao(ITokenService tokenService)
{
    public async Task<AcessoEstacaoDto> EmitirAsync(
        string tenant,
        Guid vinculoId,
        Guid estacaoId,
        Guid unidadeId,
        PapelEstacao papel,
        CancellationToken cancellationToken)
    {
        var estacao = estacaoId.ToString();
        var papelClaim = PapeisDaEstacao.ValorDaClaim(papel);
        Claim[] claims =
        [
            new(JwtRegisteredClaimNames.Sub, estacao),
            new(ClaimTypes.NameIdentifier, estacao),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(ClaimConstants.Tenant, tenant),
            new(PafClaims.Papel, papelClaim),
            new(PafClaims.Estacao, estacao),
            new(PafClaims.Unidade, unidadeId.ToString()),
            new(PafClaims.Vinculo, vinculoId.ToString()),
        ];

        // Lifetime null = JwtOptions.AccessTokenMinutes: the device renews by its long-lived secret, so the
        // access token stays as short as a user's.
        var (accessToken, expiresAt) = await tokenService
            .IssueAccessOnlyAsync(estacao, claims, lifetime: null, cancellationToken)
            .ConfigureAwait(false);

        return new AcessoEstacaoDto(papelClaim, accessToken, expiresAt);
    }
}
