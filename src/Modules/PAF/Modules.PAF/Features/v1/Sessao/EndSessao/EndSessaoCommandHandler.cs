using FSH.Framework.Core.Context;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.v1.Sessao;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Sessao.EndSessao;

/// <summary>
/// Logout pelo servidor (seção 5): solta licença e guichê e revoga a sessão do kit. Soltar vem antes de revogar
/// porque, se a revogação falhar, a sessão sem marca viva já é recusada na requisição seguinte. Vale também com
/// a marca vencida: o middleware dispensa a marca nesta rota, para a sessão ser revogada no servidor.
/// </summary>
public sealed class EndSessaoCommandHandler(
    ICurrentUser usuarioAtual,
    Licencas licencas,
    EstadoSessao estado,
    ISessionService sessoes,
    ConexoesPaf conexoes) : ICommandHandler<EndSessaoCommand>
{
    public const string Motivo = "PAF: logout";

    public async ValueTask<Unit> Handle(EndSessaoCommand command, CancellationToken cancellationToken)
    {
        var sessaoId = ClaimDeSessao.DasClaims(usuarioAtual.GetUserClaims() ?? [])
            ?? throw new UnauthorizedException(SessionCheckMiddleware.MensagemSessaoInvalida);

        // Without rows (root, user with no unit) the release writes nothing.
        if (usuarioAtual.GetTenant() is { Length: > 0 } tenantId
            && !string.Equals(tenantId, MultitenancyConstants.Root.Id, StringComparison.Ordinal))
        {
            await licencas.LiberarAsync(tenantId, sessaoId, cancellationToken);
            await estado.RemoverAsync(tenantId, sessaoId, cancellationToken);
        }

        await sessoes.RevokeSessionAsync(sessaoId, usuarioAtual.GetUserId().ToString(), Motivo, cancellationToken);
        if (usuarioAtual.GetTenant() is { Length: > 0 } tenant)
        {
            await conexoes.EncerrarSessaoAsync(tenant, sessaoId, cancellationToken);
        }

        return Unit.Value;
    }
}
