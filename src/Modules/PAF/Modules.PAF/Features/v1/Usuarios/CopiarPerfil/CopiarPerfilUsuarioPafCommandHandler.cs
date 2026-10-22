using System.Net;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Usuarios;
using FSH.Modules.PAF.Contracts.v1.UsuariosUnidade;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Usuarios.Permissoes;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Usuarios.CopiarPerfil;

/// <summary>
/// RF08 "copiar perfil": the target gets the source's PAF product roles and stored scope (kind, list, order). Both
/// users must fit in the caller's scope, or copying would hand out a scope the caller does not have (RF08). The scope
/// goes through the scope edit itself, so its grant rules, its revocation and its optional cancel apply unchanged.
/// The individual permissions (C15) are copied too, so the target ends with the source's exact set.
/// </summary>
public sealed class CopiarPerfilUsuarioPafCommandHandler(
    PafDbContext db,
    EscopoUsuario escopo,
    PapeisDoUsuarioPaf papeis,
    PermissoesIndividuais individuais,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor,
    IMediator mediator)
    : ICommandHandler<CopiarPerfilUsuarioPafCommand, Guid>
{
    public const string MensagemOrigemSemEscopo = "O usuário de origem não tem unidade para copiar.";

    public async ValueTask<Guid> Handle(CopiarPerfilUsuarioPafCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await escopo.ExigirAlcanceSobreAsync(command.UsuarioOrigemId, cancellationToken).ConfigureAwait(false);
        await escopo.ExigirAlcanceSobreAsync(command.UsuarioId, cancellationToken).ConfigureAwait(false);

        var papeisDaOrigem = await papeis.DoUsuarioAsync(command.UsuarioOrigemId, cancellationToken).ConfigureAwait(false);
        foreach (var papel in papeisDaOrigem)
        {
            await papeis.ExigirConcedivelAsync(papel, cancellationToken).ConfigureAwait(false);
        }

        var individuaisDaOrigem = await individuais.DoUsuarioAsync(command.UsuarioOrigemId, cancellationToken).ConfigureAwait(false);
        await individuais.ExigirAlteracaoPermitidaAsync(command.UsuarioId, individuaisDaOrigem, cancellationToken).ConfigureAwait(false);

        var unidades = await db.UsuariosUnidade.AsNoTracking()
            .Where(u => u.UsuarioId == command.UsuarioOrigemId)
            .OrderBy(u => u.Ordem)
            .Select(u => u.UnidadeId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (unidades.Count == 0)
        {
            throw new CustomException(MensagemOrigemSemEscopo, (IEnumerable<string>?)null, HttpStatusCode.Conflict);
        }

        bool empresa = await escopo.EscopoEmpresaAsync(command.UsuarioOrigemId, cancellationToken).ConfigureAwait(false);
        await mediator.Send(
                new SetUsuarioUnidadesCommand(command.UsuarioId, unidades, empresa) { CancelarAtendimentosEmCurso = command.CancelarAtendimentosEmCurso },
                cancellationToken)
            .ConfigureAwait(false);

        // Roles after the scope: a scope the caller may not grant is refused before any role moves.
        await papeis.DefinirAsync(command.UsuarioId, papeisDaOrigem, cancellationToken).ConfigureAwait(false);
        await CopiarIndividuaisAsync(command.UsuarioId, individuaisDaOrigem, cancellationToken).ConfigureAwait(false);
        return command.UsuarioId;
    }

    // Same lock and check as the per-user grant: a save racing the copy is either seen or waits.
    private Task CopiarIndividuaisAsync(Guid usuarioId, IReadOnlyList<string> permissoes, CancellationToken ct)
    {
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id
            ?? throw new InvalidOperationException("Cópia de perfil sem tenant resolvido.");
        return individuais.DefinirSobTravaAsync(tenantId, usuarioId, permissoes, ct);
    }
}
