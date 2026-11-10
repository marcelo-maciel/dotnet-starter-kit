using System.Net;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.v1.Usuarios;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Sessao;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Usuarios.DefinirSenha;

/// <summary>
/// RF08 (C16): the administrator defines the password of a user without e-mail of his own (D14) or who lost it. The
/// password goes first, so a refusal by the kit's policy or history (400) leaves his sessions alive; then every
/// session of his is revoked through the PAF path, as in the deactivation, because whoever held the old password may
/// still be logged in with it. The password is a kit write that cannot join a PAF transaction, so the impersonation act
/// (D28) goes in its own PAF transaction right after it, before the revocations, as C15 does: a revocation that fails
/// then answers 500 with the password changed and its act already recorded.
/// </summary>
public sealed class DefinirSenhaUsuarioPafCommandHandler(
    PafDbContext db,
    EscopoUsuario escopo,
    IUserPasswordService senhas,
    IUserPermissionService permissoes,
    ISessionService sessoes,
    RevogacaoSessao revogacao,
    AtosImpersonacao atos,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : ICommandHandler<DefinirSenhaUsuarioPafCommand, Guid>
{
    public const string MensagemPropriaSenha = "Para trocar a sua própria senha, use a troca de senha do seu perfil.";
    public const string MensagemAlvoComPermissaoAMais = "O usuário tem permissão que você não tem.";

    public async ValueTask<Guid> Handle(DefinirSenhaUsuarioPafCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();

        // Without the current password, a stolen token would otherwise be enough to take over the account for good.
        var chamador = escopo.ObterUsuarioId();
        if (chamador == command.UsuarioId)
        {
            throw new CustomException(MensagemPropriaSenha, (IEnumerable<string>?)null, HttpStatusCode.BadRequest);
        }

        await escopo.ExigirAlcanceSobreAsync(command.UsuarioId, cancellationToken).ConfigureAwait(false);

        // Defining a password is logging in as that user: refused when he holds a permission the caller lacks (the kit
        // Admin of the tenant, for one), the same ceiling C12 puts on granting a role.
        var doChamador = (await permissoes.GetPermissionsAsync(chamador.ToString(), cancellationToken).ConfigureAwait(false) ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var doAlvo = await permissoes.GetPermissionsAsync(command.UsuarioId.ToString(), cancellationToken).ConfigureAwait(false) ?? [];
        if (!doAlvo.All(doChamador.Contains))
        {
            throw new ForbiddenException(MensagemAlvoComPermissaoAMais);
        }

        await senhas.SetPasswordByAdminAsync(command.UsuarioId.ToString(), command.Senha, cancellationToken).ConfigureAwait(false);

        await using (var transacao = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await atos.GravarAsync(null, cancellationToken).ConfigureAwait(false);
            await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // Kit sessions with no licence row (an expired mark) are revoked too: they would come back by refresh.
        var ativas = (await sessoes.GetUserSessionsForAdminAsync(command.UsuarioId.ToString(), cancellationToken).ConfigureAwait(false))
            .Where(s => s.IsActive)
            .Select(s => s.Id);
        var comLicenca = await db.AtividadesSessao.AsNoTracking()
            .Where(a => a.UsuarioId == command.UsuarioId)
            .Select(a => a.SessaoId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var sessaoId in ativas.Union(comLicenca))
        {
            await revogacao.RevogarAsync(tenantId, sessaoId, command.UsuarioId, RevogacaoSessao.MotivoSenhaDefinida, cancellationToken)
                .ConfigureAwait(false);
        }

        return command.UsuarioId;
    }
}
