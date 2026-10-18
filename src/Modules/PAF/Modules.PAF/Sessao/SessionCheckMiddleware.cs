using System.Security.Claims;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.DTOs;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.Authorization;
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
/// Credencial de estação tem ramo próprio, fora deste middleware; da impersonação, aqui só a barreira de
/// <see cref="ImpersonacaoPaf"/> (act_tenant raiz nas rotas do PAF).
/// </summary>
internal sealed class SessionCheckMiddleware(RequestDelegate next)
{
    public const string MensagemSessaoInvalida = "Sessão encerrada. Entre novamente.";
    public const string MensagemLicencaExpirada = "A licença da sessão expirou. Renove o acesso.";

    public async Task InvokeAsync(HttpContext context)
    {
        var usuario = context.User;
        if (usuario.Identity?.IsAuthenticated != true
            || usuario.HasClaim(c => c.Type == PafClaims.Papel))
        {
            await next(context);
            return;
        }

        // Impersonation has no kit session (the kit checks its grant per request); the PAF adds only the barrier.
        if (ImpersonacaoPaf.Impersonando(usuario))
        {
            ImpersonacaoPaf.ConferirRequisicao(context);
            await next(context);
            return;
        }

        var servicos = context.RequestServices;
        var ct = context.RequestAborted;
        var tenantClaim = usuario.FindFirstValue(ClaimConstants.Tenant);
        var raiz = string.Equals(tenantClaim, MultitenancyConstants.Root.Id, StringComparison.Ordinal);

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

        var tenantResolvido = servicos.GetRequiredService<IMultiTenantContextAccessor<AppTenantInfo>>()
            .MultiTenantContext?.TenantInfo?.Id;
        var sessao = raiz && !string.Equals(tenantResolvido, MultitenancyConstants.Root.Id, StringComparison.Ordinal)
            ? await SessaoNoTenantRaizAsync(servicos, sessaoId, ct)
            : await servicos.GetRequiredService<ISessionService>().GetSessionAsync(sessaoId, ct);
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

    // Root operator scoped to a client tenant by header: the session lives in root, and the request's Identity
    // context runs under the client tenant filter, so it is read in a scope set to the root tenant (the kit's
    // UserPermissionService does the same for a home tenant).
    private static async Task<UserSessionDto?> SessaoNoTenantRaizAsync(IServiceProvider servicos, Guid sessaoId, CancellationToken ct)
    {
        await using var escopo = servicos.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var raiz = await escopo.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
            .GetAsync(MultitenancyConstants.Root.Id)
            ?? throw new UnauthorizedException(MensagemSessaoInvalida);
        escopo.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(raiz);
        return await escopo.ServiceProvider.GetRequiredService<ISessionService>().GetSessionAsync(sessaoId, ct);
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

        var estado = servicos.GetRequiredService<EstadoSessao>();
        var unidadeCorrente = await estado.LerUnidadeAsync(tenantId, sessaoId, ct) ?? unidadeLicencaId;

        // RF08: taking the current unit out of the scope revokes the session. The scope edit revokes in the same
        // act; this check covers a revocation that failed half-way. A disabled unit is not checked here: sessions
        // already open in it run until they end (RF08, RF22).
        var revogacao = servicos.GetRequiredService<RevogacaoSessao>();
        if (!await servicos.GetRequiredService<EscopoUsuario>().ContemAsync(usuarioId, unidadeCorrente, ct))
        {
            await revogacao.RevogarAsync(tenantId, sessaoId, usuarioId, RevogacaoSessao.MotivoUnidadeRetirada, ct);
            throw new UnauthorizedException(RevogacaoSessao.MensagemUnidadeRetirada);
        }

        servicos.GetRequiredService<SessaoCorrente>().Definir(tenantId, sessaoId, usuarioId, unidadeLicencaId);
        servicos.GetRequiredService<IUnidadeCorrente>().Definir(unidadeCorrente);

        if (await licencas.RenovarMarcaAsync(tenantId, sessaoId, ct))
        {
            // Deactivation in a kit screen revokes nothing; the renewal is where the PAF notices it, at most one
            // renewal cycle later (seção 9, ADR 0008).
            var perfil = await servicos.GetRequiredService<IUserProfileService>().GetAsync(usuarioId.ToString(), ct);
            if (!perfil.IsActive)
            {
                await revogacao.RevogarAsync(tenantId, sessaoId, usuarioId, RevogacaoSessao.MotivoUsuarioInativo, ct);
                throw new UnauthorizedException(MensagemSessaoInvalida);
            }

            await estado.RenovarAsync(tenantId, sessaoId, ct);
        }

        await next(context);
    }
}
