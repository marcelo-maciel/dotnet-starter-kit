using System.Net;
using System.Security.Claims;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Identity.Claims;
using FSH.Modules.PAF.Contracts;
using FSH.Modules.PAF.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.Escopo;

/// <summary>
/// Fills <see cref="IUnidadeCorrente"/> for the request (ADR 0003, ADR 0006). A station credential
/// brings its unidade in the <see cref="PafClaims.Unidade"/> claim. A person sends
/// <see cref="Cabecalho"/>, accepted only when the unidade is in the person's <c>UsuarioUnidade</c> scope.
/// Without either, nothing is set and the unit filter returns no rows.
/// </summary>
public sealed partial class UnidadeCorrenteMiddleware(RequestDelegate next, ILogger<UnidadeCorrenteMiddleware> logger)
{
    // ponytail: the person's current unidade comes from this header for now; the session state in Valkey
    // (ADR 0006, arquitetura seção 9) replaces it and the header goes away.
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

        string cabecalho = context.Request.Headers[Cabecalho].ToString();
        string? claim = principal.FindFirstValue(PafClaims.Unidade);
        Guid? unidadeId = claim is null
            ? await ResolverPelaPessoaAsync(context, principal, cabecalho).ConfigureAwait(false)
            : ResolverPelaEstacao(claim, cabecalho);

        if (unidadeId is { } id)
        {
            context.RequestServices.GetRequiredService<IUnidadeCorrente>().Definir(id);
        }

        await next(context).ConfigureAwait(false);
    }

    // A station belongs to one unidade and never switches, so a header naming another one is refused,
    // not silently ignored.
    private Guid ResolverPelaEstacao(string claim, string cabecalho)
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

        return unidadeId;
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

        // UsuarioUnidade has no unit filter (tenant only), so it is readable before the current unit exists.
        var db = context.RequestServices.GetRequiredService<PafDbContext>();
        bool noEscopo = await db.UsuariosUnidade.AsNoTracking()
            .AnyAsync(u => u.UsuarioId == usuarioId && u.UnidadeId == unidadeId, context.RequestAborted)
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
