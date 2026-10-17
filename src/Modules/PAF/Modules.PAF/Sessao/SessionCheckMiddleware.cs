using System.Security.Claims;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Sessao.EndSessao;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// Conferência de sessão por requisição do token de pessoa (RF22, seção 9, ADR 0008 "Conferência de tenant e de
/// sessão"): claim de sessão, UserSession ativa e marca de sessão viva em <c>AtividadeSessao</c>, conferidas no
/// PostgreSQL. Define a unidade corrente (estado no Valkey, ou a unidade da licença sem ele) e renova a marca.
/// Impersonação e credencial de estação têm ramos próprios, fora deste middleware.
/// </summary>
internal sealed class SessionCheckMiddleware(RequestDelegate next)
{
    // Station credentials carry the unit in this claim (estacao slice); their branch checks VinculoEstacao.
    public const string ClaimUnidadeEstacao = "paf_unidade";

    public const string MensagemSessaoInvalida = "Sessão encerrada. Entre novamente.";
    public const string MensagemLicencaExpirada = "A licença da sessão expirou. Renove o acesso.";

    public async Task InvokeAsync(HttpContext context)
    {
        var usuario = context.User;
        if (usuario.Identity?.IsAuthenticated != true
            || usuario.HasClaim(c => c.Type == ClaimConstants.ActorSubject)
            || usuario.HasClaim(c => c.Type == ClaimUnidadeEstacao))
        {
            await next(context);
            return;
        }

        var servicos = context.RequestServices;
        var ct = context.RequestAborted;
        var tenantClaim = usuario.FindFirstValue(ClaimConstants.Tenant);
        var raiz = string.Equals(tenantClaim, MultitenancyConstants.Root.Id, StringComparison.Ordinal);
        var tenantResolvido = servicos.GetRequiredService<IMultiTenantContextAccessor<AppTenantInfo>>()
            .MultiTenantContext?.TenantInfo?.Id;

        // Root operator scoped to a client tenant by header: the session lives in root, and the Identity
        // queries below would run under the client tenant filter. Not checked in this slice.
        if (raiz && !string.Equals(tenantResolvido, MultitenancyConstants.Root.Id, StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        if (ClaimDeSessao.DasClaims(usuario.Claims) is not { } sessaoId)
        {
            // Root tokens without the claim come from the kit's end-impersonation path (not in ADR 0008).
            if (raiz)
            {
                await next(context);
                return;
            }

            throw new UnauthorizedException(MensagemSessaoInvalida);
        }

        var sessao = await servicos.GetRequiredService<ISessionService>().GetSessionAsync(sessaoId, ct);
        if (sessao is not { IsActive: true })
        {
            throw new UnauthorizedException(MensagemSessaoInvalida);
        }

        // The root tenant has no PAF unit and takes no licence (RF23). Logout needs only a live session: with an
        // expired mark it must still revoke the session server-side, or the refresh token stays revivable.
        var endpoint = context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
        if (raiz || string.Equals(endpoint, EndSessaoEndpoint.NomeEndpoint, StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        await ConferirLicencaAsync(context, tenantClaim!, sessaoId, Guid.Parse(sessao.UserId!), ct);
    }

    private async Task ConferirLicencaAsync(HttpContext context, string tenantId, Guid sessaoId, Guid usuarioId, CancellationToken ct)
    {
        var servicos = context.RequestServices;
        var licencas = servicos.GetRequiredService<Licencas>();
        var unidadeLicenca = await licencas.UnidadeDaMarcaVivaAsync(tenantId, sessaoId, ct);
        if (unidadeLicenca is not { } unidadeLicencaId)
        {
            // A user with no unit is not a PAF user: no licence, and the unit filter stays closed for him.
            if (!await licencas.UsuarioDoPafAsync(usuarioId, ct))
            {
                await next(context);
                return;
            }

            // The refresh path re-occupies through the same check as the login (seção 9, sessão revivida).
            throw new UnauthorizedException(MensagemLicencaExpirada);
        }

        servicos.GetRequiredService<SessaoCorrente>().Definir(tenantId, sessaoId, usuarioId, unidadeLicencaId);

        var estado = servicos.GetRequiredService<EstadoSessao>();
        var unidadeCorrente = await estado.LerUnidadeAsync(tenantId, sessaoId, ct) ?? unidadeLicencaId;
        servicos.GetRequiredService<IUnidadeCorrente>().Definir(unidadeCorrente);

        if (await licencas.RenovarMarcaAsync(tenantId, sessaoId, ct))
        {
            await estado.RenovarAsync(tenantId, sessaoId, ct);
        }

        await next(context);
    }
}
