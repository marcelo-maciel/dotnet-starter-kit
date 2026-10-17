using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.DTOs;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Contracts.v1.Tokens.TokenGeneration;
using Mediator;

namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// Login com licença e sessão única (RF22, ADR 0008 "Interceptação do login"). A credencial é validada antes de
/// tocar na licença e a exceção dela segue intacta, para a recusa por licença só aparecer a quem provou ser o
/// usuário (RF01). Depois: conferir saldo, emitir, revogar as sessões anteriores. O tenant raiz passa direto.
/// </summary>
internal sealed class GenerateTokenLicenseBehavior(
    IIdentityService identidade,
    ISessionService sessoes,
    Licencas licencas,
    EstadoSessao estado,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : IPipelineBehavior<GenerateTokenCommand, TokenResponse>
{
    public const string MotivoSessaoUnica = "PAF: login em outra máquina (sessão única)";

    public async ValueTask<TokenResponse> Handle(
        GenerateTokenCommand message,
        MessageHandlerDelegate<GenerateTokenCommand, TokenResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(next);

        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id;
        if (tenantId is null || string.Equals(tenantId, MultitenancyConstants.Root.Id, StringComparison.Ordinal))
        {
            return await next(message, cancellationToken);
        }

        // The kit service throws on every invalid credential; null would only come from another implementation.
        var credencial = await identidade.ValidateCredentialsAsync(
                message.Email, message.Password, message.TwoFactorCode, cancellationToken)
            ?? throw new UnauthorizedAccessException("Invalid credentials.");
        var subject = credencial.Subject;

        await using var reserva = await licencas.ReservarAsync(tenantId, Guid.Parse(subject), cancellationToken);
        if (reserva is null)
        {
            return await next(message, cancellationToken);
        }

        var token = await next(message, cancellationToken);
        var sessaoId = ClaimDeSessao.DoToken(token.AccessToken)
            ?? throw new InvalidOperationException("Identity issued an access token without the session claim (ADR 0008).");

        var anteriores = (await sessoes.GetUserSessionsForAdminAsync(subject, cancellationToken))
            .Select(s => s.Id)
            .Where(id => id != sessaoId)
            .ToList();
        await reserva.ConfirmarAsync(sessaoId, anteriores, cancellationToken);

        // The admin variant: the login request is anonymous, and the self-service one checks the current user.
        foreach (var anterior in anteriores)
        {
            await sessoes.RevokeSessionForAdminAsync(anterior, subject, MotivoSessaoUnica, cancellationToken);
            await estado.RemoverAsync(tenantId, anterior, cancellationToken);
        }

        return token;
    }
}
