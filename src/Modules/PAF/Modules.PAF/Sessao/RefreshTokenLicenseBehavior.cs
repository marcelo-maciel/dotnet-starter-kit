using System.Security.Cryptography;
using System.Text;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Contracts.v1.Tokens.RefreshToken;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.TempoReal;
using Mediator;

namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// Renovação que revive uma sessão sem licença ocupada passa pela mesma cascata do login, antes de o token sair
/// (RF22, ARQ seção 9, ADR 0008 "A credencial é validada antes da cascata"): refresh token validado, licença
/// reservada, tratador do kit (rotação e emissão), confirmação. Recusada, nada muda: sem token novo, sem rotação, e o
/// refresh token antigo continua valendo para quando houver vaga. A sessão com marca viva só renova.
/// </summary>
internal sealed class RefreshTokenLicenseBehavior(
    IIdentityService identidade,
    ISessionService sessoes,
    Licencas licencas,
    EstadoSessao estado,
    AvisosTempoReal avisos,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : IPipelineBehavior<RefreshTokenCommand, RefreshTokenCommandResponse>
{
    public async ValueTask<RefreshTokenCommandResponse> Handle(
        RefreshTokenCommand message,
        MessageHandlerDelegate<RefreshTokenCommand, RefreshTokenCommandResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(next);

        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id;
        if (tenantId is null || string.Equals(tenantId, MultitenancyConstants.Root.Id, StringComparison.Ordinal))
        {
            return await next(message, cancellationToken);
        }

        // An invalid refresh token goes to the kit untouched: its own refusal and audit, no licence read (RF01).
        // A session row missing means a token from before the session claim; the per-request check refuses it.
        if (await identidade.ValidateRefreshTokenAsync(message.RefreshToken, cancellationToken) is not { } credencial
            || !Guid.TryParse(credencial.Subject, out var usuarioId)
            || await sessoes.GetSessionIdByRefreshTokenAsync(HashDoRefreshToken(message.RefreshToken), cancellationToken) is not { } sessaoId
            || await licencas.UnidadeDaMarcaVivaAsync(tenantId, sessaoId, cancellationToken) is not null)
        {
            return await next(message, cancellationToken);
        }

        var unidadeAnterior = await estado.LerUnidadeAsync(tenantId, sessaoId, cancellationToken)
            ?? await licencas.UnidadeDaUltimaMarcaAsync(tenantId, sessaoId, cancellationToken);

        await using var reserva = await licencas.ReservarAsync(tenantId, usuarioId, cancellationToken);
        if (reserva is null)
        {
            return await next(message, cancellationToken);
        }

        // Re-read under the user lock: a login on another machine may have revoked this session while the refresh
        // waited, and confirming now would delete the new session's row (sessão única).
        if (await sessoes.GetSessionAsync(sessaoId, cancellationToken) is not { IsActive: true })
        {
            throw new UnauthorizedException(SessionCheckMiddleware.MensagemSessaoInvalida);
        }

        UnidadeCorrenteAlteradaPaf? aviso = null;
        if (unidadeAnterior is { } anterior && anterior != reserva.UnidadeId)
        {
            aviso = UnidadeCorrenteAlteradaPaf.Criar(
                tenantId, OperacaoGuiche.Correlacao(), credencial.Subject, new UnidadeCorrenteAlterada(Guid.CreateVersion7(), reserva.UnidadeId));
            await avisos.GravarAsync(aviso, cancellationToken);
        }

        // A kit refusal here (session revoked, subject mismatch) disposes the reservation: nothing stays occupied.
        var resposta = await next(message, cancellationToken);
        await reserva.ConfirmarAsync(sessaoId, [], cancellationToken);

        if (aviso is not null)
        {
            // The unit chosen before (manual switch) would override the licence unit in SessionCheckMiddleware.
            await estado.RemoverAsync(tenantId, sessaoId, cancellationToken);
            await avisos.EnviarAsync(aviso, cancellationToken);
        }

        return resposta;
    }

    // Same digest the kit stores for the session (RefreshTokenCommandHandler.Sha256Short, private there): first 8
    // bytes of SHA-256, upper-case hex. Pinned by the refresh tests through the session it finds.
    private static string HashDoRefreshToken(string refreshToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)).AsSpan(0, 8));
}
