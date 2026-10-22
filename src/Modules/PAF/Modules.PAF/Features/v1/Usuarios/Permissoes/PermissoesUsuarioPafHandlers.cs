using Finbuckle.MultiTenant.Abstractions;
using FluentValidation;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.v1.Usuarios;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Sessao;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Usuarios.Permissoes;

/// <summary>The user screen's permission panel (C15): the four groups and where each permission of the user comes from.</summary>
public sealed class GetPermissoesUsuarioPafQueryHandler(EscopoUsuario escopo, PermissoesIndividuais individuais)
    : IQueryHandler<GetPermissoesUsuarioPafQuery, PermissoesUsuarioPafDto>
{
    public async ValueTask<PermissoesUsuarioPafDto> Handle(GetPermissoesUsuarioPafQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await escopo.ExigirAlcanceSobreAsync(query.UsuarioId, cancellationToken).ConfigureAwait(false);

        var dosPapeis = await individuais.DosPapeisAsync(query.UsuarioId, cancellationToken).ConfigureAwait(false);
        var proprias = await individuais.DoUsuarioAsync(query.UsuarioId, cancellationToken).ConfigureAwait(false);
        var doChamador = await individuais.DoChamadorAsync(cancellationToken).ConfigureAwait(false);
        return new PermissoesUsuarioPafDto(
            query.UsuarioId,
            GruposPermissoesPaf.Todos,
            [.. dosPapeis.Where(GruposPermissoesPaf.Nomes.Contains)],
            [.. proprias.Where(GruposPermissoesPaf.Nomes.Contains).Order(StringComparer.Ordinal)],
            [.. GruposPermissoesPaf.Nomes.Where(doChamador.Contains).Order(StringComparer.Ordinal)]);
    }
}

/// <summary>
/// RF08 per-user grant (C15). Reach (RF08 l.317) first, then the "only what the caller holds" check and the write
/// under the per-user lock of <see cref="PermissoesIndividuais.DefinirSobTravaAsync"/>. The write is a kit write that
/// cannot join a PAF transaction, so the impersonation act (D28) goes in its own PAF transaction right after it, as
/// the deactivation does: a failure there leaves the grant without its act and answers 500.
/// </summary>
public sealed class DefinirPermissoesUsuarioPafCommandHandler(
    PafDbContext db,
    EscopoUsuario escopo,
    PermissoesIndividuais individuais,
    AtosImpersonacao atos,
    IUserProfileService usuarios,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : ICommandHandler<DefinirPermissoesUsuarioPafCommand, Guid>
{
    public async ValueTask<Guid> Handle(DefinirPermissoesUsuarioPafCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id
            ?? throw new InvalidOperationException("Permissões de usuário sem tenant resolvido.");
        await escopo.ExigirAlcanceSobreAsync(command.UsuarioId, cancellationToken).ConfigureAwait(false);

        // Throws NotFound for a user outside the request's tenant, before any role exists for him.
        await usuarios.GetAsync(command.UsuarioId.ToString(), cancellationToken).ConfigureAwait(false);

        var novas = command.Permissoes.ToHashSet(StringComparer.Ordinal);
        await individuais.DefinirSobTravaAsync(tenantId, command.UsuarioId, novas, cancellationToken).ConfigureAwait(false);

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await atos.GravarAsync(null, cancellationToken).ConfigureAwait(false);
        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        return command.UsuarioId;
    }
}

public sealed class DefinirPermissoesUsuarioPafCommandValidator : AbstractValidator<DefinirPermissoesUsuarioPafCommand>
{
    public const string MensagemPermissaoForaDosGrupos = "Permissão que não pode ser concedida por usuário.";

    public DefinirPermissoesUsuarioPafCommandValidator()
    {
        RuleFor(x => x.UsuarioId).NotEmpty();
        RuleFor(x => x.Permissoes).NotNull();
        RuleForEach(x => x.Permissoes)
            .Must(p => p is not null && GruposPermissoesPaf.Nomes.Contains(p))
            .WithMessage(MensagemPermissaoForaDosGrupos);
    }
}
