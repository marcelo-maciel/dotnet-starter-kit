using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.TempoReal;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Fichas.CancelarAtendimentoDaFicha;

/// <summary>
/// RF12 cancel by whoever holds <c>FichasDoDia.Cancelar</c>, with no guichê: the way out for an abandoned or wrongly
/// emitted atendimento (requisitos seção 4). The unit comes from the request scope through the filtered read of the
/// ficha lock, so an atendimento of another unit is 404.
/// </summary>
public sealed class CancelarAtendimentoDaFichaCommandHandler(
    PafDbContext db,
    AvisosTempoReal avisos,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : ICommandHandler<CancelarAtendimentoDaFichaCommand, AtendimentoAlteradoDto>
{
    public async ValueTask<AtendimentoAlteradoDto> Handle(CancelarAtendimentoDaFichaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var cancelado = await OperacaoGuiche.CancelarAsync(db, tenantId, command.AtendimentoId, cancellationToken).ConfigureAwait(false);
        await OperacaoGuiche.ConfirmarEAvisarTodosAsync(transacao, avisos, cancelado.Avisos, cancellationToken).ConfigureAwait(false);
        return new AtendimentoAlteradoDto(cancelado.AtendimentoId, nameof(EstadoAtendimento.Cancelado), cancelado.Versao);
    }
}
