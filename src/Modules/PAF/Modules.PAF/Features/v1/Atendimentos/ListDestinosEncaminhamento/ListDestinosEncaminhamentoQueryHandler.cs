using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Atendimentos.EncaminharAtendimento;
using FSH.Modules.PAF.Features.v1.Filas;
using FSH.Modules.PAF.Sessao;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ListDestinosEncaminhamento;

/// <summary>
/// RF17 screen data: the atendimento started on the session's guichê, every active fila of the current unit
/// except its fila, each marked when outside its window by the PostgreSQL clock, and the prioridades associated
/// to it with guichê coverage (<see cref="CoberturaGuiche"/>). A read only: the forwarding re-validates all of it.
/// </summary>
public sealed class ListDestinosEncaminhamentoQueryHandler(PafDbContext db, SessaoCorrente sessao, ConcessaoCorrente concessao)
    : IQueryHandler<ListDestinosEncaminhamentoQuery, DestinosEncaminhamentoDto>
{
    public async ValueTask<DestinosEncaminhamentoDto> Handle(ListDestinosEncaminhamentoQuery query, CancellationToken cancellationToken)
    {
        var guiche = await OperacaoGuiche.ExigirAsync(db, DonoPosto.Exigir(sessao, concessao), cancellationToken);
        var origem = await (
                from a in db.Atendimentos.AsNoTracking()
                join f in db.Fichas.AsNoTracking() on a.FichaId equals f.Id
                join fila in db.Filas.AsNoTracking() on a.FilaId equals fila.Id
                where a.EstacaoId == guiche.Estacao.Id && a.Estado == EstadoAtendimento.EmAtendimento
                select new { a.Id, a.FilaId, Fila = fila.Nome, a.PrioridadeId, f.Letra, f.Numero })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw OperacaoGuiche.Conflito(EncaminharAtendimentoCommandHandler.MensagemSemAtendimentoIniciado);

        var hora = TimeOnly.FromDateTime(await PafRelogio.AgoraEmSaoPauloAsync(db, cancellationToken));
        var filas = await db.Filas.AsNoTracking()
            .Where(f => f.Ativa && f.Id != origem.FilaId)
            .OrderBy(f => f.Nome)
            .Select(f => new { f.Id, f.Nome, f.HoraInicial, f.HoraFinal })
            .ToListAsync(cancellationToken);

        var pares = CoberturaGuiche.Pares(db);
        var prioridades = await (
                from fp in db.FilaPrioridades.AsNoTracking()
                join p in db.Prioridades.AsNoTracking() on fp.PrioridadeId equals p.Id
                where p.Ativa && pares.Any(c => c.FilaId == fp.FilaId && c.PrioridadeId == fp.PrioridadeId)
                orderby fp.Ordem
                select new { fp.FilaId, p.Id, p.Nome })
            .ToListAsync(cancellationToken);
        var porFila = prioridades.ToLookup(p => p.FilaId);

        var destinos = filas
            .Select(f => new DestinoEncaminhamentoDto(
                f.Id,
                f.Nome,
                !Fila.JanelaPermiteEmissao(f.HoraInicial, f.HoraFinal, hora),
                porFila[f.Id].Select(p => new PrioridadeDestinoDto(p.Id, p.Nome)).ToList()))
            .ToList();
        return new DestinosEncaminhamentoDto(
            origem.Id, OperacaoGuiche.Senha(origem.Letra, origem.Numero), origem.FilaId, origem.Fila, origem.PrioridadeId, destinos);
    }
}
