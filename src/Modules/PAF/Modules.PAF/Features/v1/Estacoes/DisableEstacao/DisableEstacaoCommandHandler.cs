using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Filas;
using FSH.Modules.PAF.TempoReal;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estacoes.DisableEstacao;

public sealed class DisableEstacaoCommandHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    ConexoesPaf conexoes,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : ICommandHandler<DisableEstacaoCommand, EstacaoAlteradaDto>
{
    public async ValueTask<EstacaoAlteradaDto> Handle(DisableEstacaoCommand command, CancellationToken cancellationToken)
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
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var depois = await CoberturaGuiche.CombinacoesCobertasAsync(db, cancellationToken).ConfigureAwait(false);

        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (tenantAccessor.MultiTenantContext?.TenantInfo?.Id is { } tenant)
        {
            conexoes.EncerrarEstacao(tenant, entidade.Id);
        }

        return new EstacaoAlteradaDto(entidade.Id, antes.ExceptBy(depois.Select(c => c.Id), c => c.Id).ToList());
    }
}
