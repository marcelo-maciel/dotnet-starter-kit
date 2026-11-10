using FSH.Modules.PAF.Contracts.v1.Sessao;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Sessao;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Sessao.GetAvisoLicenca;

/// <summary>
/// Lê o excedente gravado na ocupação da própria sessão (D03). Sem sessão PAF (raiz, impersonação, estação) não há
/// licença e não há aviso. Só o filtro de unidade sai: a licença pode estar numa unidade que não é a corrente depois
/// de uma troca, e a linha é da própria sessão.
/// </summary>
public sealed class GetAvisoLicencaQueryHandler(PafDbContext db, SessaoCorrente sessao)
    : IQueryHandler<GetAvisoLicencaQuery, AvisoLicencaDto?>
{
    public async ValueTask<AvisoLicencaDto?> Handle(GetAvisoLicencaQuery query, CancellationToken cancellationToken)
    {
        if (sessao.SessaoId is not { } sessaoId)
        {
            return null;
        }

        return await (
                from c in db.ConsumosLicenca.AsNoTracking().IgnoreQueryFilters([PafQueryFilters.Unidade])
                join u in db.Unidades.AsNoTracking() on c.UnidadeId equals u.Id
                where c.SessaoId == sessaoId && c.Tipo == TipoConsumoLicenca.Excedente
                orderby c.RegistradoEm descending, c.Id descending
                select new AvisoLicencaDto(c.Id, c.UnidadeId, u.Nome, c.Ocupadas, c.Limite, c.RegistradoEm))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
