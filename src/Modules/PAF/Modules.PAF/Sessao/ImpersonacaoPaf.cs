using System.Security.Claims;
using System.Text.RegularExpressions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Shared.Multitenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// Barreira de impersonação do PAF (RF22, RNF15, seção 9): só a impersonação feita pela Codefi, com
/// <c>act_tenant</c> do tenant raiz, opera as telas e operações do PAF. O kit deixa o administrador do cliente
/// impersonar usuários do próprio tenant, e com a operação completa sem licença ele operaria guichês sem pagar.
/// </summary>
public static partial class ImpersonacaoPaf
{
    public const string MensagemRecusada = "Impersonação feita pelo administrador do cliente não opera o PAF.";

    internal static bool Impersonando(ClaimsPrincipal principal) =>
        !string.IsNullOrEmpty(principal.FindFirstValue(ClaimConstants.ActorSubject));

    /// <summary>Missing <c>act_tenant</c> counts as not root: the barrier fails closed.</summary>
    internal static bool DaCodefi(ClaimsPrincipal principal) =>
        string.Equals(principal.FindFirstValue(ClaimConstants.ActorTenant), MultitenancyConstants.Root.Id, StringComparison.Ordinal);

    /// <summary>
    /// HTTP side of the barrier. Kit screens keep accepting the client admin's impersonation; only the PAF routes
    /// refuse it, before authorization, so no handler runs and nothing is written.
    /// </summary>
    internal static void ConferirRequisicao(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (DaCodefi(context.User) || !RotaDoPaf(context.Request))
        {
            return;
        }

        LogRecusada(
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ImpersonacaoPaf)),
            context.User.FindFirstValue(ClaimConstants.Tenant),
            context.User.FindFirstValue(ClaimConstants.ActorTenant));
        throw new ForbiddenException(MensagemRecusada);
    }

    /// <summary>
    /// Every PAF route, the hub included, lives under <c>api/v{n}/paf</c>. Tested on the request path, not on endpoint
    /// metadata, so a route added later without the metadata cannot slip through; routing matches the path
    /// case-insensitively, and so does this.
    /// </summary>
    internal static bool RotaDoPaf(HttpRequest request) => RotaPaf().IsMatch(request.Path.Value ?? string.Empty);

    [GeneratedRegex("^/api/v[^/]+/paf(?:/|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex RotaPaf();

    [LoggerMessage(Level = LogLevel.Warning, Message = "[PAF] impersonation refused: tenant {TenantId}, actor tenant {ActorTenantId}")]
    private static partial void LogRecusada(ILogger logger, string? tenantId, string? actorTenantId);
}
