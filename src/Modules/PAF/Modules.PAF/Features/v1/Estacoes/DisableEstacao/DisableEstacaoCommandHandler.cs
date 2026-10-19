using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.Features.v1.Filas;
using FSH.Modules.PAF.TempoReal;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estacoes.DisableEstacao;

/// <summary>
/// Deactivation with real effect (RF04, L18): in one transaction the station stops covering, its device links are
/// revoked, its guichê is released when nothing is in progress on it, and the atendimento the dialog chose is
/// cancelled by RF12. After the commit the devices are closed and the released session leaves the guichê group.
/// </summary>
public sealed class DisableEstacaoCommandHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    ConexoesPaf conexoes,
    AvisosTempoReal avisos,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : ICommandHandler<DisableEstacaoCommand, EstacaoDesativadaDto>
{
    public async ValueTask<EstacaoDesativadaDto> Handle(DisableEstacaoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var unidadeId = unidadeCorrente.Exigir();
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // An inactive guichê covers nothing: disabling may remove the last coverage of a combination (RF02).
        await CoberturaGuiche.TravarParaAlteracaoAsync(db, tenantId, unidadeId, cancellationToken).ConfigureAwait(false);

        var entidade = await db.Estacoes
            .FirstOrDefaultAsync(e => e.Id == command.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Estacao {command.Id} não encontrada.");

        var antes = await CoberturaGuiche.CombinacoesCobertasAsync(db, cancellationToken).ConfigureAwait(false);
        entidade.Desativar();
        // Flushed before the guichê step: the F01 release reads Ativa inside this transaction.
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var depois = await CoberturaGuiche.CombinacoesCobertasAsync(db, cancellationToken).ConfigureAwait(false);

        // Re-enabling the station then needs a new link: a device left offline must not come back by itself.
        await RevogacaoVinculo.RevogarVigentesAsync(db, entidade.Id, cancellationToken).ConfigureAwait(false);
        var guiche = await DesativacaoEstacao.SoltarGuicheAsync(db, tenantId, entidade.Id, command.CancelarAtendimentoId, cancellationToken)
            .ConfigureAwait(false);

        await OperacaoGuiche.ConfirmarEAvisarTodosAsync(transacao, avisos, guiche.Avisos, cancellationToken).ConfigureAwait(false);
        await conexoes.EncerrarEstacaoAsync(tenantId, entidade.Id, cancellationToken).ConfigureAwait(false);
        // ponytail: a grant-owned link is released in the database but its connections stay in the guichê group until
        // the grant's next selection or end; a grant-scoped move arrives with the guichê impersonation (S11).
        if (guiche.SessaoSolta is { } sessaoSolta)
        {
            await conexoes.MoverSessaoParaGuicheAsync(tenantId, sessaoSolta, null, cancellationToken).ConfigureAwait(false);
        }

        return new EstacaoDesativadaDto(
            entidade.Id, antes.ExceptBy(depois.Select(c => c.Id), c => c.Id).ToList(), guiche.EmCurso, guiche.Liberado);
    }
}
