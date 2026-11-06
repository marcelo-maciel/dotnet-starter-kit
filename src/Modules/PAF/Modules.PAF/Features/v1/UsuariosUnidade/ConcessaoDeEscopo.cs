using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.UsuariosUnidade.SetUsuarioUnidades;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.UsuariosUnidade;

/// <summary>
/// What a caller may grant (RF08, arquitetura seção 5), shared by the scope edit and the user cadastro (C12): only the
/// tenant admin grants escopo empresa, and he grants any ACTIVE unidade of the tenant; anyone else grants only inside
/// his own effective scope and only to a user whose whole scope fits in it.
/// </summary>
public sealed class ConcessaoDeEscopo(PafDbContext db, EscopoUsuario escopo)
{
    /// <summary>Grant to an existing user: the reach over him is part of the rule for a non-admin.</summary>
    public async Task ExigirAsync(Guid usuarioAlvoId, IReadOnlyCollection<Guid> unidades, bool escopoEmpresa, CancellationToken ct)
    {
        if (await AdministradorConcedeAsync(unidades, escopoEmpresa, ct).ConfigureAwait(false))
        {
            return;
        }

        await escopo.ExigirAlcanceSobreAsync(usuarioAlvoId, ct).ConfigureAwait(false);
        await ExigirDentroDoEscopoDoChamadorAsync(unidades, ct).ConfigureAwait(false);
    }

    /// <summary>Grant to a user about to be created, checked before the kit user exists: he has no scope to reach yet.</summary>
    public async Task ExigirParaUsuarioNovoAsync(IReadOnlyCollection<Guid> unidades, bool escopoEmpresa, CancellationToken ct)
    {
        if (await AdministradorConcedeAsync(unidades, escopoEmpresa, ct).ConfigureAwait(false))
        {
            return;
        }

        await ExigirDentroDoEscopoDoChamadorAsync(unidades, ct).ConfigureAwait(false);
    }

    // True when the caller is the tenant admin and the grant passed his rule; false sends a non-admin to the subset rule.
    private async Task<bool> AdministradorConcedeAsync(IReadOnlyCollection<Guid> unidades, bool escopoEmpresa, CancellationToken ct)
    {
        bool administraTenant = await escopo.AdministraTenantAsync(ct).ConfigureAwait(false);
        if (escopoEmpresa && !administraTenant)
        {
            throw new ForbiddenException(SetUsuarioUnidadesCommandHandler.MensagemSoEmpresaConcedeEmpresa);
        }

        if (!administraTenant)
        {
            return false;
        }

        // Unidade carries only the tenant filter: an id of another tenant counts as missing.
        var ativas = await db.Unidades.AsNoTracking()
            .CountAsync(u => unidades.Contains(u.Id) && u.Ativa, ct)
            .ConfigureAwait(false);
        if (ativas != unidades.Distinct().Count())
        {
            throw new CustomException(SetUsuarioUnidadesCommandHandler.MensagemUnidadeInvalida, (IEnumerable<string>?)null, System.Net.HttpStatusCode.BadRequest);
        }

        return true;
    }

    private async Task ExigirDentroDoEscopoDoChamadorAsync(IReadOnlyCollection<Guid> unidades, CancellationToken ct)
    {
        var doChamador = (await escopo.EscopoEfetivoAsync(escopo.ObterUsuarioId(), ct).ConfigureAwait(false)).ToHashSet();
        if (!unidades.All(doChamador.Contains))
        {
            throw new ForbiddenException(SetUsuarioUnidadesCommandHandler.MensagemForaDoEscopoDeQuemConcede);
        }
    }
}
