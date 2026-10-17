using FSH.Framework.Core.Context;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.v1.Sessao;
using FSH.Modules.PAF.Sessao;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Sessao.EncerrarSessao;

/// <summary>
/// Logout pelo servidor (seção 5): solta licença e guichê e revoga a sessão do kit. Soltar vem antes de revogar
/// porque, se a revogação falhar, a sessão sem marca viva já é recusada na requisição seguinte.
/// </summary>
public sealed class EncerrarSessaoCommandHandler(
    ICurrentUser usuarioAtual,
    SessaoCorrente sessao,
    Licencas licencas,
    EstadoSessao estado,
    ISessionService sessoes) : ICommandHandler<EncerrarSessaoCommand>
{
    public const string Motivo = "PAF: logout";

    public async ValueTask<Unit> Handle(EncerrarSessaoCommand command, CancellationToken cancellationToken)
    {
        var sessaoId = ClaimDeSessao.DasClaims(usuarioAtual.GetUserClaims() ?? [])
            ?? throw new UnauthorizedException(SessionCheckMiddleware.MensagemSessaoInvalida);

        if (sessao is { TenantId: { } tenantId, SessaoId: { } sessaoPaf })
        {
            await licencas.LiberarAsync(tenantId, sessaoPaf, cancellationToken);
            await estado.RemoverAsync(tenantId, sessaoPaf, cancellationToken);
        }

        await sessoes.RevokeSessionAsync(sessaoId, usuarioAtual.GetUserId().ToString(), Motivo, cancellationToken);
        return Unit.Value;
    }
}
