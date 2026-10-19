using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Guiches;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Guiches.LiberarGuiche;

/// <summary>
/// The manager's release of a stuck guichê (RF04, ARQ seção 5 "quem grava a liberação"): whoever holds it, live or not.
/// Refused while an atendimento is in progress on it, because that one would be left with no guichê to end it from;
/// it is released by RF12 instead.
/// </summary>
public sealed class LiberarGuicheCommandHandler(
    PafDbContext db,
    ConexoesPaf conexoes,
    AtosImpersonacao atos,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : ICommandHandler<LiberarGuicheCommand>
{
    public const string MensagemNaoEncontrado = "Guichê não encontrado na unidade corrente.";

    public async ValueTask<Unit> Handle(LiberarGuicheCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();

        // Unit-filtered: a guichê of another unit is not the manager's to release.
        var existe = await db.Estacoes.AsNoTracking().AnyAsync(e => e.Id == command.EstacaoId, cancellationToken).ConfigureAwait(false);
        if (!existe)
        {
            throw new NotFoundException(MensagemNaoEncontrado);
        }

        Guid? sessaoSolta = null;
        await using (var transacao = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            // Locks the link before the in-progress check, as the call takes it: no call commits a Chamado after it.
            // The link was read unlocked: if its session let it go meanwhile, it may already sit on another guichê,
            // and moving its connections would pull them out of that one.
            if (await VinculosGuiche.AtivoDaEstacaoAsync(db, command.EstacaoId, cancellationToken).ConfigureAwait(false) is { } vinculo
                && await VinculosGuiche.SoltarSemAtendimentoEmCursoAsync(db, tenantId, vinculo, cancellationToken).ConfigureAwait(false))
            {
                sessaoSolta = vinculo.SessaoId;
            }

            // Raw SQL leaves no tracked entity: the impersonation act is written here, also for the no-op release.
            await atos.GravarAsync(atendimentoId: null, cancellationToken).ConfigureAwait(false);
            await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // ponytail: a grant-owned link leaves the database only; its connections follow at the grant's next selection (S11).
        if (sessaoSolta is { } sessao)
        {
            await conexoes.MoverSessaoParaGuicheAsync(tenantId, sessao, null, cancellationToken).ConfigureAwait(false);
        }

        return Unit.Value;
    }
}
