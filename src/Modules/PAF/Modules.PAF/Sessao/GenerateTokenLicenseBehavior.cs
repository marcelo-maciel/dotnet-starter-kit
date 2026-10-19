using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.DTOs;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Contracts.v1.Tokens.TokenGeneration;
using FSH.Modules.PAF.TempoReal;
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
    ConexoesPaf conexoes,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : IPipelineBehavior<GenerateTokenCommand, TokenResponse>
{
    public const string MotivoSessaoUnica = "PAF: login em outra máquina (sessão única)";

    public const string MensagemSemUnidade =
        "Seu usuário não tem unidade de atendimento. Procure o administrador da empresa.";

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
            ?? throw new UnauthorizedException("Invalid credentials.");
        var subject = credencial.Subject;

        // A client-tenant user with no unidade would get a token with no licence and no unit (RF23, C03): the
        // tenant creation gives the initial admin his unidade, and every other user gets one from the cadastro.
        await using var reserva = await licencas.ReservarAsync(tenantId, Guid.Parse(subject), cancellationToken)
            ?? throw new ForbiddenException(MensagemSemUnidade);

        var token = await next(message, cancellationToken);
        var sessaoId = ClaimDeSessao.DoToken(token.AccessToken)
            ?? throw new InvalidOperationException("Identity issued an access token without the session claim (ADR 0008).");

        var anteriores = (await sessoes.GetUserSessionsForAdminAsync(subject, cancellationToken))
            .Select(s => s.Id)
            .Where(id => id != sessaoId)
            .ToList();
        // Revoked before the commit, still under the unit lock: a refresh of an earlier session waiting on that
        // lock then finds its session revoked and stops (RefreshTokenLicenseBehavior), instead of re-occupying
        // and deleting this session's row, which would let two machines knock each other out in turns. Two
        // DbContexts, no atomicity: if the commit fails after this, the user is left with no session and logs
        // in again, the safe side.
        // The admin variant: the login request is anonymous, and the self-service one checks the current user.
        foreach (var anterior in anteriores)
        {
            await sessoes.RevokeSessionForAdminAsync(anterior, subject, MotivoSessaoUnica, cancellationToken);
            await estado.RemoverAsync(tenantId, anterior, cancellationToken);
        }

        await reserva.ConfirmarAsync(sessaoId, anteriores, cancellationToken);
        foreach (var anterior in anteriores)
        {
            await conexoes.EncerrarSessaoAsync(tenantId, anterior, cancellationToken);
        }

        return token;
    }
}
