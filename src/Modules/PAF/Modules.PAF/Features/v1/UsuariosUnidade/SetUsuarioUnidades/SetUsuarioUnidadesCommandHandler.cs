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
/// Scope is the boundary of granting and of editing (RF08, arquitetura seção 5): only the tenant admin
/// (<see cref="EscopoUsuario.AdministraTenantAsync"/>) grants escopo empresa, and he grants any active unidade of the
/// tenant and edits any user; anyone else grants only inside his own
/// effective scope and only edits a user whose whole scope fits in it. Reordering is an UPDATE of
/// <c>Ordem</c>, never delete and recreate (RF08). Taking out the unidade a live session is operating revokes that
/// session in the same act (RF08).
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
    public const string MensagemSoEmpresaConcedeEmpresa = "Só o administrador de escopo empresa concede escopo empresa.";
    public const string MensagemForaDoEscopoDeQuemConcede = "Unidade fora do escopo de quem concede.";

    public async ValueTask<Guid> Handle(SetUsuarioUnidadesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id
            ?? throw new InvalidOperationException("Edição de escopo sem tenant resolvido.");

        // Throws NotFound for a user outside the request's tenant.
        await userService.GetAsync(command.UsuarioId.ToString(), cancellationToken).ConfigureAwait(false);

        IReadOnlyList<Guid> antes;
        IReadOnlyList<Guid> depois;
        await using (var transacao = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            // Two edits of the same user serialize here, so each one reads the rows the previous one committed and
            // the result is one request whole, never a mix of both lists.
            var chave = $"paf-escopo:{tenantId}:{command.UsuarioId}";
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({chave}, 0))", cancellationToken)
                .ConfigureAwait(false);

            await ExigirConcessaoAsync(command, cancellationToken).ConfigureAwait(false);

            antes = await escopo.EscopoEfetivoAsync(command.UsuarioId, cancellationToken).ConfigureAwait(false);
            await GravarAsync(command, cancellationToken).ConfigureAwait(false);
            depois = await escopo.EscopoEfetivoAsync(command.UsuarioId, cancellationToken).ConfigureAwait(false);

            // UX_UsuariosUnidade_Ordem is deferred: a duplicated principal is refused here, at commit.
            await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var retiradas = antes.Except(depois).ToHashSet();
        if (retiradas.Count > 0)
        {
            await RevogarSessoesNasRetiradasAsync(tenantId, command.UsuarioId, retiradas, cancellationToken).ConfigureAwait(false);
        }

        return command.UsuarioId;
    }

    private async Task ExigirConcessaoAsync(SetUsuarioUnidadesCommand command, CancellationToken ct)
    {
        bool administraTenant = await escopo.AdministraTenantAsync(ct).ConfigureAwait(false);
        if (command.EscopoEmpresa && !administraTenant)
        {
            throw new ForbiddenException(MensagemSoEmpresaConcedeEmpresa);
        }

        if (administraTenant)
        {
            await ExigirUnidadesAtivasAsync(command.Unidades, ct).ConfigureAwait(false);
            return;
        }

        await escopo.ExigirAlcanceSobreAsync(command.UsuarioId, ct).ConfigureAwait(false);
        var doChamador = (await escopo.EscopoEfetivoAsync(escopo.ObterUsuarioId(), ct).ConfigureAwait(false)).ToHashSet();
        if (!command.Unidades.All(doChamador.Contains))
        {
            throw new ForbiddenException(MensagemForaDoEscopoDeQuemConcede);
        }
    }

    private async Task GravarAsync(SetUsuarioUnidadesCommand command, CancellationToken ct)
    {
        var atuais = await db.UsuariosUnidade
            .Where(u => u.UsuarioId == command.UsuarioId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var ordemDesejada = command.Unidades.Select((unidadeId, ordem) => (unidadeId, ordem)).ToDictionary(x => x.unidadeId, x => x.ordem);
        foreach (var atual in atuais)
        {
            if (ordemDesejada.Remove(atual.UnidadeId, out int ordem))
            {
                atual.DefinirOrdem(ordem);
            }
            else
            {
                db.UsuariosUnidade.Remove(atual);
            }
        }

        db.UsuariosUnidade.AddRange(ordemDesejada.Select(d => UsuarioUnidade.Criar(command.UsuarioId, d.Key, d.Value)));

        // No row means "unidades selecionadas", so a list grant to a user without a profile writes none.
        var perfil = await db.PerfisUsuario.FirstOrDefaultAsync(p => p.UsuarioId == command.UsuarioId, ct).ConfigureAwait(false);
        if (!command.EscopoEmpresa)
        {
            perfil?.DefinirEscopoUnidadesSelecionadas();
        }
        else if (perfil is null)
        {
            perfil = PerfilUsuario.Criar(command.UsuarioId);
            perfil.DefinirEscopoEmpresa();
            db.PerfisUsuario.Add(perfil);
        }
        else
        {
            perfil.DefinirEscopoEmpresa();
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
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
    private async Task RevogarSessoesNasRetiradasAsync(string tenantId, Guid usuarioId, HashSet<Guid> retiradas, CancellationToken ct)
    {
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
