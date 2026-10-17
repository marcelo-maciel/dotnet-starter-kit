using System.Security.Claims;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.Conferencia;

/// <summary>
/// Conferência por requisição registrada pelo IModule.ConfigureMiddleware (ADR 0008), depois de
/// UseAuthentication e antes de UseAuthorization. Vale para o host inteiro, não só para as rotas do PAF.
/// </summary>
internal sealed partial class ConferenciaRequisicaoMiddleware(RequestDelegate next, ILogger<ConferenciaRequisicaoMiddleware> logger)
{
    /// <summary>Marca, em HttpContext.Items, o papel da credencial de estação conferida nesta requisição.</summary>
    internal const string ChaveEstacaoConferida = "paf:estacao-conferida";

    public async Task InvokeAsync(HttpContext context, IUnidadeCorrente unidadeCorrente, PafDbContext db)
    {
        var usuario = context.User;
        if (usuario.Identity?.IsAuthenticated != true)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var papel = usuario.FindFirstValue(PafClaims.Papel);
        ConferirTenant(context, usuario, papel is not null);

        if (papel is not null)
        {
            await ConferirCredencialDeEstacaoAsync(context, usuario, papel, unidadeCorrente, db).ConfigureAwait(false);
        }

        await next(context).ConfigureAwait(false);
    }

    // The kit resolves an authenticated request by the token's tenant claim and silently ignores a differing
    // header, so without this refusal tenant isolation would depend on the browser sending the right header
    // (arquitetura seção 5). Root operators are exempt because the kit's override serves them; a station
    // credential never is, even in the root tenant.
    private void ConferirTenant(HttpContext context, ClaimsPrincipal usuario, bool ehEstacao)
    {
        var tenantDaClaim = usuario.FindFirstValue(ClaimConstants.Tenant);
        if (!ehEstacao && string.Equals(tenantDaClaim, MultitenancyConstants.Root.Id, StringComparison.Ordinal))
        {
            return;
        }

        var pedidos = context.Request.Headers[MultitenancyConstants.Identifier]
            .Concat(context.Request.Query["tenant"]);
        foreach (var pedido in pedidos)
        {
            if (!string.IsNullOrEmpty(pedido) && !string.Equals(pedido, tenantDaClaim, StringComparison.Ordinal))
            {
                LogTenantDivergente(logger, context.Request.Path.Value);
                throw new ForbiddenException("The requested tenant does not match the authenticated tenant.");
            }
        }
    }

    // A revoked link or a deactivated station must stop the device on its next request (RF04), so the
    // credential is checked against VinculoEstacao on every request, not only when it was issued.
    private async Task ConferirCredencialDeEstacaoAsync(
        HttpContext context,
        ClaimsPrincipal usuario,
        string papel,
        IUnidadeCorrente unidadeCorrente,
        PafDbContext db)
    {
        if (!Guid.TryParse(usuario.FindFirstValue(PafClaims.Unidade), out var unidadeId)
            || !Guid.TryParse(usuario.FindFirstValue(PafClaims.Estacao), out var estacaoId)
            || !Guid.TryParse(usuario.FindFirstValue(PafClaims.Vinculo), out var vinculoId)
            || (papel != PafClaims.PapelEmissao && papel != PafClaims.PapelPainel))
        {
            throw new UnauthorizedException();
        }

        // The station's unit is fixed by its credential (ADR 0006); it must be set before the first query
        // because the unit filter is fail-closed.
        unidadeCorrente.Definir(unidadeId);

        var emissao = papel == PafClaims.PapelEmissao;
        var vigente = await db.VinculosEstacao
            .AsNoTracking()
            .Where(v => v.Id == vinculoId && v.EstacaoId == estacaoId && v.RevogadoEm == null)
            .Where(v => emissao ? v.CredencialEmissaoHash != null : v.CredencialPainelHash != null)
            .AnyAsync(v => db.Estacoes.Any(e => e.Id == v.EstacaoId && e.Ativa), context.RequestAborted)
            .ConfigureAwait(false);

        if (!vigente)
        {
            LogCredencialRecusada(logger, estacaoId, vinculoId);
            throw new UnauthorizedException();
        }

        context.Items[ChaveEstacaoConferida] = papel;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "PAF refused request to {Path}: tenant header or query differs from the token tenant claim")]
    private static partial void LogTenantDivergente(ILogger logger, string? path);

    [LoggerMessage(Level = LogLevel.Information, Message = "PAF refused station credential: estacao={EstacaoId} vinculo={VinculoId} revoked, inactive or unknown")]
    private static partial void LogCredencialRecusada(ILogger logger, Guid estacaoId, Guid vinculoId);
}
