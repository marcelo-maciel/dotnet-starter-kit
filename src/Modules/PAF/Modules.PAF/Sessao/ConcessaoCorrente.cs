using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Shared.Identity.Claims;

namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// Concessão de impersonação da Codefi lida do token (seção 9, RF22). A concessão é identificada pelo <c>jti</c>
/// do token, que o kit grava em <c>ImpersonationGrant.Jti</c> como Guid no formato "N": é esse Guid que vai em
/// <c>SessaoEstacao.ConcessaoId</c> e na chave do estado no Valkey, porque o <c>Id</c> da concessão não está no token.
/// </summary>
public sealed record ConcessaoImpersonacao(string TenantId, Guid ConcessaoId, Guid UsuarioId, DateTimeOffset ExpiraEm)
{
    /// <summary>Nulo quando o token não é de impersonação ou não traz tenant, jti, sub e exp bem formados.</summary>
    public static ConcessaoImpersonacao? DoToken(ClaimsPrincipal? principal)
    {
        if (principal is null || string.IsNullOrEmpty(principal.FindFirstValue(ClaimConstants.ActorSubject)))
        {
            return null;
        }

        var tenantId = principal.FindFirstValue(ClaimConstants.Tenant);
        if (string.IsNullOrWhiteSpace(tenantId)
            || !Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Jti), out var concessaoId)
            || !Guid.TryParse(principal.GetUserId(), out var usuarioId)
            || !long.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Exp), out var exp))
        {
            return null;
        }

        return new ConcessaoImpersonacao(tenantId, concessaoId, usuarioId, DateTimeOffset.FromUnixTimeSeconds(exp));
    }
}

/// <summary>
/// Concessão já conferida pelo <see cref="SessionCheckMiddleware"/> nesta requisição; vazia fora da impersonação.
/// A impersonação não preenche <see cref="SessaoCorrente"/>: quem exige sessão de pessoa continua recusando.
/// </summary>
public sealed class ConcessaoCorrente
{
    public ConcessaoImpersonacao? Valor { get; private set; }

    public void Definir(ConcessaoImpersonacao concessao) => Valor = concessao;
}
