using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Contracts.v1.Tokens.RefreshToken;
using FSH.Modules.PAF.TempoReal;
using Mediator;

namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// Renovação que revive uma sessão sem licença ocupada passa pela mesma conferência do login (RF22, seção 9).
/// A sessão com marca viva só renova, sem mexer em licença nem em guichê. O tratador do kit roda primeiro e
/// valida refresh token e sessão; a recusa por licença vem depois e revoga a sessão, então o token emitido
/// nunca chega ao cliente nem vale numa requisição.
/// </summary>
internal sealed class RefreshTokenLicenseBehavior(
    ISessionService sessoes,
    Licencas licencas,
    ConexoesPaf conexoes,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : IPipelineBehavior<RefreshTokenCommand, RefreshTokenCommandResponse>
{
    public const string MotivoSemLicenca = "PAF: sessão revivida sem saldo de licença";

    public async ValueTask<RefreshTokenCommandResponse> Handle(
        RefreshTokenCommand message,
        MessageHandlerDelegate<RefreshTokenCommand, RefreshTokenCommandResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        var resposta = await next(message, cancellationToken);
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id;
        if (tenantId is null || string.Equals(tenantId, MultitenancyConstants.Root.Id, StringComparison.Ordinal))
        {
            return resposta;
        }

        // No claim: a session created before the claim existed; the per-request check refuses that token.
        if (ClaimDeSessao.DoToken(resposta.Token) is not { } sessaoId
            || await licencas.UnidadeDaMarcaVivaAsync(tenantId, sessaoId, cancellationToken) is not null)
        {
            return resposta;
        }

        var usuarioId = ClaimDeSessao.UsuarioDoToken(resposta.Token);
        ReservaLicenca? reserva;
        try
        {
            reserva = await licencas.ReservarAsync(tenantId, usuarioId, cancellationToken);
        }
        catch (ForbiddenException)
        {
            await sessoes.RevokeSessionForAdminAsync(sessaoId, usuarioId.ToString(), MotivoSemLicenca, cancellationToken);
            conexoes.EncerrarSessao(tenantId, sessaoId);
            throw;
        }

        if (reserva is null)
        {
            return resposta;
        }

        await using (reserva)
        {
            // Re-read under the unit lock: a login on another machine may have revoked this session while the
            // refresh waited for the lock, and confirming now would delete the new session's row (sessão única).
            if (await sessoes.GetSessionAsync(sessaoId, cancellationToken) is not { IsActive: true })
            {
                throw new UnauthorizedException(SessionCheckMiddleware.MensagemSessaoInvalida);
            }

            await reserva.ConfirmarAsync(sessaoId, [], cancellationToken);
        }

        return resposta;
    }
}
