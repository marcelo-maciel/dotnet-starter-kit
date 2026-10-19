using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Fichas.ConsultarFicha;

/// <summary>
/// RF18: the ficha of the current unit by its printed senha and operational day (D02), with all its atendimentos.
/// Plain read under the unit filter, so a senha of another unit is the same 404 as an unknown one. Cancel and
/// reabilitar stay on the F04 and F05 endpoints; this screen only reads.
/// </summary>
public sealed class ConsultarFichaQueryHandler(PafDbContext db, IUnidadeCorrente unidadeCorrente)
    : IQueryHandler<ConsultarFichaQuery, IReadOnlyList<FichaConsultadaDto>>
{
    public const string MensagemNaoEncontrada = "Não há ficha com essa senha nesta unidade na data informada.";

    // Same folding as the cadastro (LetraDaSenha.Normalizar) applies to the stored letter, so " a12 " finds A12.
    internal static string Normalizar(string senha) => senha.Trim().ToUpperInvariant();

    public async ValueTask<IReadOnlyList<FichaConsultadaDto>> Handle(ConsultarFichaQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        unidadeCorrente.Exigir();
        var senha = Normalizar(query.Senha!);
        var data = query.Data!.Value;

        // The letter may hold digits (the cadastro only bounds its length), so "A12" can be A + 12 or A1 + 2:
        // compare the printed concatenation instead of splitting it here.
        var fichas = await (
                from f in db.Fichas.AsNoTracking()
                where f.DataOperacional == data && f.Letra + f.Numero == senha
                join fila in db.Filas.AsNoTracking() on f.FilaId equals fila.Id
                join prioridade in db.Prioridades.AsNoTracking() on f.PrioridadeId equals prioridade.Id
                orderby f.CriadoEm, f.Id
                select new { f.Id, f.Letra, f.Numero, f.DataOperacional, f.CriadoEm, Fila = fila.Nome, Prioridade = prioridade.Nome })
            .ToListAsync(cancellationToken);
        if (fichas.Count == 0)
        {
            throw new NotFoundException(MensagemNaoEncontrada);
        }

        var fichaIds = fichas.Select(f => f.Id).ToList();
        var atendimentos = await AtendimentosDasFichas(db, fichaIds).ToListAsync(cancellationToken);

        return fichas
            .Select(f => new FichaConsultadaDto(
                f.Id,
                OperacaoGuiche.Senha(f.Letra, f.Numero),
                f.DataOperacional,
                f.CriadoEm,
                f.Fila,
                f.Prioridade,
                atendimentos.Where(a => a.FichaId == f.Id).Select(ParaDto).ToList()))
            .ToList();
    }

    /// <summary>
    /// Every atendimento of the fichas, in creation order. Server-side as a whole (no constructor or client call in
    /// the projection), so the running time is computed by PostgreSQL; a test pins now() in the generated SQL.
    /// </summary>
    internal static IQueryable<AtendimentoConsultado> AtendimentosDasFichas(PafDbContext db, List<Guid> fichaIds) =>
        from a in db.Atendimentos.AsNoTracking()
        where fichaIds.Contains(a.FichaId)
        join fila in db.Filas.AsNoTracking() on a.FilaId equals fila.Id
        join prioridade in db.Prioridades.AsNoTracking() on a.PrioridadeId equals prioridade.Id
        join estacao in db.Estacoes.AsNoTracking() on a.EstacaoId equals (Guid?)estacao.Id into estacoes
        from estacao in estacoes.DefaultIfEmpty()
        orderby a.CriadoEm, a.Id
        select new AtendimentoConsultado
        {
            AtendimentoId = a.Id,
            FichaId = a.FichaId,
            FilaId = a.FilaId,
            Fila = fila.Nome,
            Prioridade = prioridade.Nome,
            Estado = a.Estado,
            Guiche = estacao == null ? null : estacao.Nome,
            CriadoEm = a.CriadoEm,
            ChamadoEm = a.ChamadoEm,
            IniciadoEm = a.IniciadoEm,
            FimEm = a.EncerradoEm ?? a.CanceladoEm,
            // DateTimeOffset.UtcNow translates to now(): the running time comes from the database clock.
            // Lifted subtraction: null while the service never started.
            TempoTotal = (a.EncerradoEm ?? a.CanceladoEm ?? DateTimeOffset.UtcNow) - a.IniciadoEm,
            AtendimentoOrigemId = a.AtendimentoOrigemId,
            Versao = a.Versao,
        };

    private static AtendimentoDaFichaDto ParaDto(AtendimentoConsultado a) =>
        new(a.AtendimentoId, a.FilaId, a.Fila, a.Prioridade, a.Estado.ToString(), a.Guiche, a.CriadoEm, a.ChamadoEm, a.IniciadoEm,
            a.FimEm, a.TempoTotal is { } tempo ? (long)Math.Floor(tempo.TotalSeconds) : null, a.AtendimentoOrigemId, a.Versao);
}

internal sealed class AtendimentoConsultado
{
    public Guid AtendimentoId { get; init; }
    public Guid FichaId { get; init; }
    public Guid FilaId { get; init; }
    public string Fila { get; init; } = default!;
    public string Prioridade { get; init; } = default!;
    public EstadoAtendimento Estado { get; init; }
    public string? Guiche { get; init; }
    public DateTimeOffset CriadoEm { get; init; }
    public DateTimeOffset? ChamadoEm { get; init; }
    public DateTimeOffset? IniciadoEm { get; init; }
    public DateTimeOffset? FimEm { get; init; }
    public TimeSpan? TempoTotal { get; init; }
    public Guid? AtendimentoOrigemId { get; init; }
    public int Versao { get; init; }
}
