using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Filas;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estacoes.UpdateEstacao;

public sealed class UpdateEstacaoCommandHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : ICommandHandler<UpdateEstacaoCommand, EstacaoAlteradaDto>
{
    public async ValueTask<EstacaoAlteradaDto> Handle(UpdateEstacaoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var unidadeId = unidadeCorrente.Exigir();
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Taking a fila or a prioridade out of a guichê may remove the last coverage of a combination (RF02).
        await CoberturaGuiche.TravarParaAlteracaoAsync(db, tenantId, unidadeId, cancellationToken).ConfigureAwait(false);

        var estacao = await db.Estacoes
            .FirstOrDefaultAsync(e => e.Id == command.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Estação {command.Id} não encontrada.");

        await ListasDaEstacao.ValidarAsync(db, estacao.Tipo, command.Filas, command.Prioridades, cancellationToken).ConfigureAwait(false);

        var antes = await CoberturaGuiche.CombinacoesCobertasAsync(db, cancellationToken).ConfigureAwait(false);
        estacao.Renomear(command.Nome.Trim());
        await ListasDaEstacao.SincronizarAsync(db, estacao, command.Filas, command.Prioridades, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var depois = await CoberturaGuiche.CombinacoesCobertasAsync(db, cancellationToken).ConfigureAwait(false);

        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new EstacaoAlteradaDto(estacao.Id, antes.ExceptBy(depois.Select(c => c.Id), c => c.Id).ToList());
    }
}
