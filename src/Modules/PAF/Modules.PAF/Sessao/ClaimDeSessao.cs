using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// Claim de sessão emitida pelo Identity (ADR 0008), "sid" no JWT. O mapeamento de entrada do JwtBearer não a
/// renomeia: no principal autenticado ela continua "sid" (conferido no teste da fatia de login).
/// </summary>
internal static class ClaimDeSessao
{
    public static Guid? DasClaims(IEnumerable<Claim> claims) =>
        Guid.TryParse(claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sid)?.Value, out var id) ? id : null;

    // The token was minted by the handler in this same pipeline, so it is read, not validated.
    public static Guid? DoToken(string accessToken) => DasClaims(new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims);

    public static Guid UsuarioDoToken(string accessToken) =>
        Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Subject);
}
