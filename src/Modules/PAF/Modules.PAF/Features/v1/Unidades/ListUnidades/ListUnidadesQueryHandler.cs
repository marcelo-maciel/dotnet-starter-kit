using FSH.Modules.PAF.Contracts.v1.Unidades;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Unidades.ListUnidades;

/// <summary>
/// The unidades of the caller's effective scope, principal first: what the unit selector offers (ADR 0006), each
/// with its contract in force, read-only (RF23). The contracts are read across the scope's unidades, so this is
/// an explicit cross-unit path: only the unit filter goes off, by name, and the tenant filter stays.
/// </summary>
public sealed class ListUnidadesQueryHandler(PafDbContext db, EscopoUsuario escopo)
    : IQueryHandler<ListUnidadesQuery, IReadOnlyList<UnidadeDto>>
{
    private static readonly string[] SoOFiltroDeUnidade = [PafQueryFilters.Unidade];

    public async ValueTask<IReadOnlyList<UnidadeDto>> Handle(ListUnidadesQuery query, CancellationToken cancellationToken)
    {
        var ordem = await escopo.EscopoEfetivoAsync(escopo.ObterUsuarioId(), cancellationToken).ConfigureAwait(false);
        var unidades = await db.Unidades.AsNoTracking()
            .Where(u => ordem.Contains(u.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Same validity as the licence (Licencas.CandidataAsync): a calendar day in America/Sao_Paulo by the
        // PostgreSQL clock, the latest start winning, so the screen shows the limit the login will apply.
        var hoje = await db.Database
            .SqlQuery<DateOnly>($"""SELECT (now() AT TIME ZONE 'America/Sao_Paulo')::date AS "Value" """)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        var contratos = await db.ContratosUnidade.IgnoreQueryFilters(SoOFiltroDeUnidade).AsNoTracking()
            .Where(c => ordem.Contains(c.UnidadeId)
                && c.InicioVigencia <= hoje
                && (c.FimVigencia == null || c.FimVigencia >= hoje))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var vigentes = contratos
            .GroupBy(c => c.UnidadeId)
            .ToDictionary(g => g.Key, g => g.MaxBy(c => c.InicioVigencia)!);

        var posicao = ordem.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return [.. unidades
            .OrderBy(u => posicao[u.Id])
            .Select(u => new UnidadeDto(
                u.Id, u.Nome, u.DocumentoFiscal, u.Ativa, u.CriadoEm,
                vigentes.TryGetValue(u.Id, out var c)
                    ? new ContratoVigenteResumoDto(c.Plano, c.LimiteSessoes, c.FimVigencia)
                    : null))];
    }
}
