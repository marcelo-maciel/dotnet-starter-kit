using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Sessao;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ChamarFichaVip;

/// <summary>
/// The filas of the session guichê's role, in the station's order: the VIP form's choices. The attendant has no
/// Filas.View, and a fila outside the guichê role would be refused by the call's predicate anyway.
/// </summary>
public sealed class ListFilasVipQueryHandler(PafDbContext db, SessaoCorrente sessao)
    : IQueryHandler<ListFilasVipQuery, IReadOnlyList<FilaVipDto>>
{
    public async ValueTask<IReadOnlyList<FilaVipDto>> Handle(ListFilasVipQuery query, CancellationToken cancellationToken)
    {
        var guiche = await OperacaoGuiche.ExigirAsync(db, sessao, cancellationToken);
        return await (
                from ef in db.EstacaoFilas.AsNoTracking()
                join f in db.Filas.AsNoTracking() on ef.FilaId equals f.Id
                where ef.EstacaoId == guiche.Estacao.Id && ef.Papel == PapelEstacao.Guiche
                orderby ef.Ordem, f.Nome
                select new FilaVipDto(f.Id, f.Nome))
            .ToListAsync(cancellationToken);
    }
}
