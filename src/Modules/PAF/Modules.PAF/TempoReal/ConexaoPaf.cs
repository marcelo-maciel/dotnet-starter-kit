using System.Security.Claims;
using FSH.Framework.Shared.Constants;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Sessao;

namespace FSH.Modules.PAF.TempoReal;

/// <summary>
/// Who is on the other end of a hub connection, read only from the validated token. The server, not
/// the client, decides the groups (seção 7): nothing here comes from the query string.
/// </summary>
public abstract record ConexaoPaf(string TenantId)
{
    /// <summary>Null when the principal is neither a well-formed station credential nor a person.</summary>
    public static ConexaoPaf? Ler(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var tenantId = principal.FindFirstValue(ClaimConstants.Tenant);
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        // The presence of the role claim alone selects the station path: a token that claims to be a
        // station but is malformed is refused, never downgraded to a person connection.
        var papel = principal.FindFirstValue(PafClaims.Papel);
        if (papel is not null)
        {
            return LerEstacao(principal, tenantId, papel);
        }

        var usuarioId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(usuarioId) || usuarioId == Guid.Empty.ToString())
        {
            return null;
        }

        var impersonacao = !string.IsNullOrEmpty(principal.FindFirstValue(ClaimConstants.ActorSubject));
        return new ConexaoPessoa(tenantId, usuarioId, impersonacao, ClaimDeSessao.DasClaims(principal.Claims));
    }

    private static ConexaoEstacao? LerEstacao(ClaimsPrincipal principal, string tenantId, string papel)
    {
        if (papel is not (PafPapeisEstacao.Emissao or PafPapeisEstacao.Painel))
        {
            return null;
        }

        if (!Guid.TryParse(principal.FindFirstValue(PafClaims.Estacao), out var estacaoId) || estacaoId == Guid.Empty)
        {
            return null;
        }

        if (!Guid.TryParse(principal.FindFirstValue(PafClaims.Unidade), out var unidadeId) || unidadeId == Guid.Empty)
        {
            return null;
        }

        if (!Guid.TryParse(principal.FindFirstValue(PafClaims.Vinculo), out var vinculoId) || vinculoId == Guid.Empty)
        {
            return null;
        }

        return new ConexaoEstacao(tenantId, papel, estacaoId, unidadeId, vinculoId);
    }
}

public sealed record ConexaoEstacao(string TenantId, string Papel, Guid EstacaoId, Guid UnidadeId, Guid VinculoId) : ConexaoPaf(TenantId);

/// <summary>
/// <paramref name="Impersonacao"/>: the token carries <c>act_sub</c> (support acting as the user).
/// <paramref name="SessaoId"/>: the kit session (<c>sid</c>); absent on impersonation and on the root tokens of the
/// kit's end-impersonation path, which SessionCheckMiddleware also lets through.
/// </summary>
public sealed record ConexaoPessoa(string TenantId, string UsuarioId, bool Impersonacao, Guid? SessaoId) : ConexaoPaf(TenantId);
