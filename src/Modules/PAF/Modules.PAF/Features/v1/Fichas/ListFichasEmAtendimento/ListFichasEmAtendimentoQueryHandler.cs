using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Fichas.ListFichasEmAtendimento;

internal sealed record LinhaEmAtendimento(
    Guid AtendimentoId,
    Guid FichaId,
    string Letra,
    int Numero,
    Guid FilaId,
    string Fila,
    string Prioridade,
    Guid? EstacaoId,
    string? Guiche,
    EstadoAtendimento Estado,
    DateTimeOffset ChamadoEm,
    DateTimeOffset? IniciadoEm,
    TimeSpan Permanencia,
    int Versao);

/// <summary>
/// RF13: what is in progress in the current unit, so a supervisor spots the atendimento a guichê abandoned and cancels
/// it (RF12, F04). No date predicate: the abandoned one is exactly the one that crossed midnight (requisitos seção 4).
/// Every table goes through the unit filter, so no current unit = no rows.
/// </summary>
public sealed class ListFichasEmAtendimentoQueryHandler(PafDbContext db)
    : IQueryHandler<ListFichasEmAtendimentoQuery, FichasEmAtendimentoDto>
{
    public const string MensagemFilaNaoEncontrada = "Fila não encontrada nesta unidade.";

    public async ValueTask<FichasEmAtendimentoDto> Handle(ListFichasEmAtendimentoQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var filas = await db.Filas.AsNoTracking()
            .OrderBy(f => f.Nome)
            .Select(f => new FilaDoFiltroDto(f.Id, f.Nome))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        // A fila of another unit is refused instead of answering an empty list that would read as "nobody stuck".
        if (query.FilaId is { } filaPedida && filas.TrueForAll(f => f.Id != filaPedida))
        {
            throw new NotFoundException(MensagemFilaNaoEncontrada);
        }

        var linhas = await Consulta(db, query.FilaId).ToListAsync(cancellationToken).ConfigureAwait(false);

        var atendimentos = linhas
            .Select(l => new FichaEmAtendimentoDto(
                l.AtendimentoId, l.FichaId, OperacaoGuiche.Senha(l.Letra, l.Numero), l.FilaId, l.Fila, l.Prioridade, l.EstacaoId, l.Guiche,
                l.Estado.ToString(), l.ChamadoEm, l.IniciadoEm, (long)Math.Floor(l.Permanencia.TotalSeconds), l.Versao))
            .ToList();
        return new FichasEmAtendimentoDto(atendimentos.Count, atendimentos, filas);
    }

    // DateTimeOffset.UtcNow inside the query is translated to now(): the stay is measured on the PostgreSQL clock, the
    // same one that stamped ChamadoEm, never against the API host's clock (pinned by the test on ToQueryString).
    internal static IQueryable<LinhaEmAtendimento> Consulta(PafDbContext db, Guid? filaId)
    {
        var emCurso = db.Atendimentos.AsNoTracking()
            .Where(a => a.Estado == EstadoAtendimento.Chamado || a.Estado == EstadoAtendimento.EmAtendimento);
        if (filaId is { } fila)
        {
            emCurso = emCurso.Where(a => a.FilaId == fila);
        }

        return from a in emCurso
               join f in db.Fichas on a.FichaId equals f.Id
               join fi in db.Filas on a.FilaId equals fi.Id
               join p in db.Prioridades on a.PrioridadeId equals p.Id
               join e in db.Estacoes on a.EstacaoId equals (Guid?)e.Id into estacoes
               from e in estacoes.DefaultIfEmpty()
               orderby a.ChamadoEm, a.Id
               select new LinhaEmAtendimento(
                   a.Id, a.FichaId, f.Letra, f.Numero, a.FilaId, fi.Nome, p.Nome, a.EstacaoId, e == null ? null : e.Nome, a.Estado,
                   a.ChamadoEm!.Value, a.IniciadoEm, DateTimeOffset.UtcNow - a.ChamadoEm!.Value, a.Versao);
    }
}
