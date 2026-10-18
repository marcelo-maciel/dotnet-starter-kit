using System.Net;
using System.Security.Claims;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Identity.Claims;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Sessao;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.Escopo;

/// <summary>
/// Last source of <see cref="IUnidadeCorrente"/> for the request (ADR 0003, ADR 0006). A station credential's
/// unidade is set by <c>ConferenciaRequisicaoMiddleware</c>; here a <see cref="Cabecalho"/> naming another
/// unidade is refused. A person with a PAF session has the unidade set by <c>SessionCheckMiddleware</c>, and
/// the header is ignored. Only a person without one (root tenant, root operator in a client tenant) uses the
/// header, accepted when the unidade is in the person's effective scope (<see cref="EscopoUsuario"/>). An impersonation
/// token (<c>act_sub</c>) never uses the header: its unidade is the grant's own state. Without any source,
/// nothing is set and the unit filter returns no rows.
/// </summary>
public sealed partial class UnidadeCorrenteMiddleware(RequestDelegate next, ILogger<UnidadeCorrenteMiddleware> logger)
{
    // ponytail: the header serves only callers the session does not cover (root tenant, root operator in a
    // client tenant); it goes away when the session state of ADR 0006 covers them too.
    public const string Cabecalho = "X-Paf-Unidade";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var principal = context.User;
        if (principal.Identity?.IsAuthenticated != true)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var unidadeCorrente = context.RequestServices.GetRequiredService<IUnidadeCorrente>();
        string cabecalho = context.Request.Headers[Cabecalho].ToString();
        // A station token is recognised by its role, like the other PAF middlewares: one missing its unidade
        // is refused instead of falling through to the person path.
        if (principal.HasClaim(c => c.Type == PafClaims.Papel))
        {
            ConferirCabecalhoDaEstacao(principal.FindFirstValue(PafClaims.Unidade), cabecalho);
        }
        else if (ImpersonacaoPaf.Impersonando(principal))
        {
            // The grant's unit comes only from its own state (SessionCheckMiddleware); the header is ignored, as for
            // a person with a PAF session, even when the grant's scope is empty and nothing was set.
        }
        else if (unidadeCorrente.UnidadeId is null
            && await ResolverPelaPessoaAsync(context, principal, cabecalho).ConfigureAwait(false) is { } unidadeId)
        {
            unidadeCorrente.Definir(unidadeId);
        }

        await next(context).ConfigureAwait(false);
    }

    // A station belongs to one unidade and never switches, so a header naming another one is refused,
    // not silently ignored.
    private void ConferirCabecalhoDaEstacao(string? claim, string cabecalho)
    {
        if (!Guid.TryParse(claim, out var unidadeId) || unidadeId == Guid.Empty)
        {
            throw new ForbiddenException("Credencial de estação sem unidade válida.");
        }

        if (cabecalho.Length > 0 && !string.Equals(cabecalho, claim, StringComparison.OrdinalIgnoreCase))
        {
            LogEstacaoFora(logger, unidadeId);
            throw new ForbiddenException("A estação não opera outra unidade.");
        }
    }

    private async Task<Guid?> ResolverPelaPessoaAsync(HttpContext context, ClaimsPrincipal principal, string cabecalho)
    {
        if (cabecalho.Length == 0)
        {
            return null;
        }

        if (!Guid.TryParse(cabecalho, out var unidadeId) || unidadeId == Guid.Empty)
        {
            throw new CustomException($"Cabeçalho {Cabecalho} inválido.", (IEnumerable<string>?)null, HttpStatusCode.BadRequest);
        }

        if (!Guid.TryParse(principal.GetUserId(), out var usuarioId))
        {
            throw new ForbiddenException("Unidade fora do escopo do usuário.");
        }

        // The scope tables have no unit filter (tenant only), so they are readable before the current unit exists.
        bool noEscopo = await context.RequestServices.GetRequiredService<EscopoUsuario>()
            .ContemAsync(usuarioId, unidadeId, context.RequestAborted)
            .ConfigureAwait(false);
        if (!noEscopo)
        {
            LogForaDoEscopo(logger, usuarioId, unidadeId);
            throw new ForbiddenException("Unidade fora do escopo do usuário.");
        }

        return unidadeId;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "[PAF] unidade {UnidadeId} refused: outside the scope of user {UsuarioId}")]
    private static partial void LogForaDoEscopo(ILogger logger, Guid usuarioId, Guid unidadeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[PAF] station of unidade {UnidadeId} sent a different unidade header")]
    private static partial void LogEstacaoFora(ILogger logger, Guid unidadeId);
}
