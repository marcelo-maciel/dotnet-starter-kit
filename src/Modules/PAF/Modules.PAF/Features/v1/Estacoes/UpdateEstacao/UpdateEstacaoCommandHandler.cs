using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Filas;
using FSH.Modules.PAF.Parametros;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estacoes.UpdateEstacao;

public sealed class UpdateEstacaoCommandHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor,
    LeitorParametros parametros)
    : ICommandHandler<UpdateEstacaoCommand, EstacaoAlteradaDto>
{
    public async ValueTask<EstacaoAlteradaDto> Handle(UpdateEstacaoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var unidadeId = unidadeCorrente.Exigir();
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();
        var fonteMinima = await parametros.LerAsync(CatalogoParametros.FonteMinimaTotemPx, unidadeId, cancellationToken).ConfigureAwait(false);

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Taking a fila or a prioridade out of a guichê may remove the last coverage of a combination (RF02).
        await CoberturaGuiche.TravarParaAlteracaoAsync(db, tenantId, unidadeId, cancellationToken).ConfigureAwait(false);

        // Two edits of the same station replace its join rows from what each one read; serialized, the second one
        // reads the first one's lists and the result is one of them whole. The unit coverage lock above also orders
        // them today, but it exists for the emission, not for this; the row lock is this write's own guarantee.
        var travadas = await db.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM paf."Estacoes"
             WHERE "TenantId" = {tenantId} AND "UnidadeId" = {unidadeId} AND "Id" = {command.Id}
               FOR UPDATE
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (travadas.Count == 0)
        {
            throw new NotFoundException($"Estação {command.Id} não encontrada.");
        }

        var estacao = await db.Estacoes
            .FirstOrDefaultAsync(e => e.Id == command.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Estação {command.Id} não encontrada.");

        ConfiguracaoDaEstacao.Validar(estacao.Tipo, command.Configuracao, fonteMinima);
        await ListasDaEstacao.ValidarAsync(db, estacao.Tipo, command.Filas, command.Prioridades, cancellationToken).ConfigureAwait(false);

        var antes = await CoberturaGuiche.CombinacoesCobertasAsync(db, cancellationToken).ConfigureAwait(false);
        estacao.Renomear(command.Nome.Trim());
        estacao.DefinirRamal(command.Ramal);
        estacao.DefinirConfiguracao(ConfiguracaoDaEstacao.Serializar(command.Configuracao));
        await ListasDaEstacao.SincronizarAsync(db, estacao, command.Filas, command.Prioridades, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var depois = await CoberturaGuiche.CombinacoesCobertasAsync(db, cancellationToken).ConfigureAwait(false);

        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new EstacaoAlteradaDto(estacao.Id, antes.ExceptBy(depois.Select(c => c.Id), c => c.Id).ToList());
    }
}
