using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.v1.Usuarios;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Usuarios.DesativarUsuario;

/// <summary>
/// RF22 "a desativação é uma operação do PAF": the kit only marks the user inactive, so this operation deactivates
/// him in the kit and revokes every session of his through the PAF path (licence, guichê, Valkey state, kit session,
/// connections) in the same act. The kit's own rules come first (only a kit Admin deactivates, never himself, never
/// another kit Admin), so a refusal there leaves nothing changed; the PAF reach check (RF08) runs before all of it.
/// </summary>
public sealed class DesativarUsuarioPafCommandHandler(
    PafDbContext db,
    EscopoUsuario escopo,
    IUserStatusService situacao,
    ISessionService sessoes,
    RevogacaoSessao revogacao,
    AtendimentosEmCursoDoUsuario emCurso,
    AtosImpersonacao atos,
    AvisosTempoReal avisos,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : ICommandHandler<DesativarUsuarioPafCommand, Guid>
{
    public async ValueTask<Guid> Handle(DesativarUsuarioPafCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();

        await escopo.ExigirAlcanceSobreAsync(command.UsuarioId, cancellationToken).ConfigureAwait(false);
        await situacao.ToggleStatusAsync(false, command.UsuarioId.ToString(), cancellationToken).ConfigureAwait(false);

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
            await revogacao.RevogarAsync(tenantId, sessaoId, command.UsuarioId, RevogacaoSessao.MotivoUsuarioInativo, cancellationToken)
                .ConfigureAwait(false);
        }

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AvisoPaf> cancelamentos = [];
        if (command.CancelarAtendimentosEmCurso)
        {
            var unidades = (await emCurso.ListarAsync(command.UsuarioId, cancellationToken).ConfigureAwait(false))
                .Select(a => a.UnidadeId)
                .ToHashSet();
            cancelamentos = await emCurso.CancelarAsync(tenantId, command.UsuarioId, unidades, cancellationToken).ConfigureAwait(false);
        }

        // The deactivation itself is a kit write; the impersonation act (D28) goes in this PAF transaction.
        await atos.GravarAsync(null, cancellationToken).ConfigureAwait(false);
        await OperacaoGuiche.ConfirmarEAvisarTodosAsync(transacao, avisos, cancelamentos, cancellationToken).ConfigureAwait(false);
        return command.UsuarioId;
    }
}
