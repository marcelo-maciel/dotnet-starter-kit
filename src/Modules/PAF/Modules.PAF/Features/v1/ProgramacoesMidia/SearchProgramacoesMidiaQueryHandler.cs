using FSH.Framework.Persistence;
using FSH.Framework.Shared.Persistence;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.ProgramacoesMidia;

/// <summary>
/// RF10 search, in SQL so paging counts what the filters keep. The end of a programação is computed from the mídia's
/// current duration inside the query, the same arithmetic as <c>AgendaMidia</c>, so the hour filter and the painel
/// agree on the window. Unit filter on both tables: another unidade's rows never show.
/// </summary>
public sealed class SearchProgramacoesMidiaQueryHandler(PafDbContext db)
    : IQueryHandler<SearchProgramacoesMidiaQuery, PagedResponse<ProgramacaoMidiaDto>>
{
    public async ValueTask<PagedResponse<ProgramacaoMidiaDto>> Handle(SearchProgramacoesMidiaQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var linhas =
            from p in db.ProgramacoesMidia.AsNoTracking()
            join m in db.Midias.AsNoTracking() on p.MidiaId equals m.Id
            select new
            {
                P = p,
                M = m,
                Inicio = p.HoraInicial.Hour * 3600 + p.HoraInicial.Minute * 60 + p.HoraInicial.Second,
                Total = (long)m.DuracaoSegundos * p.Repeticoes,
            };

        if (!string.IsNullOrWhiteSpace(query.Nome))
        {
            var padrao = $"%{EscaparLike(query.Nome.Trim())}%";
            linhas = linhas.Where(l => EF.Functions.ILike(l.P.Nome, padrao, EscapeLike));
        }

        if (query.Dias is { } dias)
        {
            var mascara = (DiasSemana)dias;
            linhas = linhas.Where(l => (l.P.Dias & mascara) != 0);
        }

        if (query.Hora is { } hora)
        {
            long segundo = hora.Hour * 3600 + hora.Minute * 60 + hora.Second;
            linhas = linhas.Where(l => l.Inicio <= segundo && segundo < l.Inicio + l.Total);
        }

        if (query.De is { } de)
        {
            linhas = linhas.Where(l => l.P.DataFinal >= de);
        }

        if (query.Ate is { } ate)
        {
            linhas = linhas.Where(l => l.P.DataInicial <= ate);
        }

        return await linhas
            .OrderBy(l => l.P.Nome).ThenBy(l => l.P.HoraInicial).ThenBy(l => l.P.Id)
            .Select(l => new ProgramacaoMidiaDto(
                l.P.Id, l.P.UnidadeId, l.P.MidiaId, l.M.Descricao, l.M.Ativa, l.P.Nome, l.P.DataInicial, l.P.DataFinal,
                l.P.HoraInicial, l.P.Repeticoes, (int)l.P.Dias, l.P.Ativa, l.P.Versao, l.M.DuracaoSegundos,
                l.Total, l.Inicio + l.Total))
            .ToPagedResponseAsync(query, cancellationToken)
            .ConfigureAwait(false);
    }

    // Explicit: with the two-argument ILike a typed % still matched as a wildcard (PafProgramacoesMidiaTests).
    private const string EscapeLike = "\\";

    // A name with % or _ is searched literally.
    private static string EscaparLike(string texto) =>
        texto.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
