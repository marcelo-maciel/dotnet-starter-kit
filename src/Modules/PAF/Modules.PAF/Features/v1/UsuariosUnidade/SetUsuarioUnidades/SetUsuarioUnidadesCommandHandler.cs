using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.v1.UsuariosUnidade;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Sessao;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.UsuariosUnidade.SetUsuarioUnidades;

/// <summary>
/// Scope is the boundary of granting and of editing (arquitetura seção 5): the caller only grants
/// unidades inside its own scope, and only edits a user whose whole current scope is inside it (RF08). The tenant
/// admin, who holds Unidades.Create, grants any active unidade of the tenant and edits any user.
/// Taking out the unidade a live session is operating revokes that session in the same act (RF08).
/// </summary>
public sealed class SetUsuarioUnidadesCommandHandler(
    PafDbContext db,
    EscopoUsuario escopo,
    IUserProfileService userService,
    EstadoSessao estado,
    RevogacaoSessao revogacao,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : ICommandHandler<SetUsuarioUnidadesCommand, Guid>
{
    public const string MensagemUnidadeInvalida = "Unidade inexistente ou inativa.";

    public async ValueTask<Guid> Handle(SetUsuarioUnidadesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Throws NotFound for a user outside the request's tenant.
        await userService.GetAsync(command.UsuarioId.ToString(), cancellationToken).ConfigureAwait(false);

        var atuais = await db.UsuariosUnidade
            .Where(u => u.UsuarioId == command.UsuarioId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (await escopo.AdministraTenantAsync(cancellationToken).ConfigureAwait(false))
        {
            await ExigirUnidadesAtivasAsync(command.Unidades, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var doChamador = (await escopo.UnidadesAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
            if (!atuais.All(a => doChamador.Contains(a.UnidadeId)) || !command.Unidades.All(doChamador.Contains))
            {
                throw new ForbiddenException("Unidade fora do escopo de quem concede.");
            }
        }

        var ordemDesejada = command.Unidades.Select((unidadeId, ordem) => (unidadeId, ordem)).ToDictionary(x => x.unidadeId, x => x.ordem);
        var retiradas = new HashSet<Guid>();
        foreach (var atual in atuais)
        {
            if (ordemDesejada.Remove(atual.UnidadeId, out int ordem))
            {
                atual.DefinirOrdem(ordem);
            }
            else
            {
                db.UsuariosUnidade.Remove(atual);
                retiradas.Add(atual.UnidadeId);
            }
        }

        db.UsuariosUnidade.AddRange(ordemDesejada.Select(d => UsuarioUnidade.Criar(command.UsuarioId, d.Key, d.Value)));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (retiradas.Count > 0)
        {
            await RevogarSessoesNasRetiradasAsync(command.UsuarioId, retiradas, cancellationToken).ConfigureAwait(false);
        }

        return command.UsuarioId;
    }

    // Unidade carries only the tenant filter: an id of another tenant counts as missing.
    private async Task ExigirUnidadesAtivasAsync(IReadOnlyCollection<Guid> unidades, CancellationToken ct)
    {
        var ativas = await db.Unidades.AsNoTracking()
            .CountAsync(u => unidades.Contains(u.Id) && u.Ativa, ct)
            .ConfigureAwait(false);
        if (ativas != unidades.Distinct().Count())
        {
            throw new CustomException(MensagemUnidadeInvalida, (IEnumerable<string>?)null, System.Net.HttpStatusCode.BadRequest);
        }
    }

    // Only sessions holding a licence row can be operating; one whose mark expired is refused anyway and comes
    // back through the cascade of the new scope. The current unit is the Valkey one, else the licence unit.
    private async Task RevogarSessoesNasRetiradasAsync(Guid usuarioId, HashSet<Guid> retiradas, CancellationToken ct)
    {
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id
            ?? throw new InvalidOperationException("Edição de escopo sem tenant resolvido.");
        var sessoes = await db.AtividadesSessao.AsNoTracking()
            .Where(a => a.UsuarioId == usuarioId)
            .Select(a => new { a.SessaoId, a.UnidadeId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var sessao in sessoes)
        {
            var corrente = await estado.LerUnidadeAsync(tenantId, sessao.SessaoId, ct).ConfigureAwait(false) ?? sessao.UnidadeId;
            if (retiradas.Contains(corrente))
            {
                await revogacao.RevogarAsync(tenantId, sessao.SessaoId, usuarioId, RevogacaoSessao.MotivoUnidadeRetirada, ct)
                    .ConfigureAwait(false);
            }
        }
    }
}
