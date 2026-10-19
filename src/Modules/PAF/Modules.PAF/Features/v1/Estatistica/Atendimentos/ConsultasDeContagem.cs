using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Estatistica;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using Microsoft.EntityFrameworkCore;
using static FSH.Modules.PAF.Features.v1.Estatistica.Atendimentos.EstatisticaAtendimentos;

namespace FSH.Modules.PAF.Features.v1.Estatistica.Atendimentos;

/// <summary>
/// Type 5: atendimentos per hour of their start in America/Sao_Paulo, cancelled ones out, as the legacy did
/// (RF07, 03/10/2026). The period stays on the atendimento's DataOperacional, so a start at 02:30Z counts in hour
/// 23 of the previous calendar day.
/// </summary>
public sealed class ConsultaAtendimentosPorHorarioDePico(PafDbContext db, IMultiTenantContextAccessor<AppTenantInfo> tenant) : IConsultaEstatistica
{
    public const string FusoHorario = "America/Sao_Paulo";

    public static readonly ColunaEstatisticaDto ColunaHora = new("hora", "Hora", TipoColunaEstatistica.Texto);
    public static readonly ColunaEstatisticaDto ColunaAtendimentos = new("atendimentos", "Atendimentos", TipoColunaEstatistica.Inteiro);

    public TipoEstatistica Tipo => TipoEstatistica.AtendimentosPorHorarioDePico;

    public static string RotuloDaHora(int hora) => $"{hora:00}h";

    public async Task<ResultadoEstatisticaDto> ConsultarAsync(FiltroEstatistica filtro, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        var tenantId = tenant.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();
        var unidadesIds = filtro.Unidades.ToArray();
        var filas = filtro.Filas.ToArray();
        var prioridades = filtro.Prioridades.ToArray();

        // Raw SQL because neither EF.Functions.AtTimeZone (bound to the SQL Server extension) nor
        // TimeZoneInfo.ConvertTimeBySystemTimeZoneId over a DateTimeOffset translates in this Npgsql (checked
        // 19/10/2026). Raw SQL skips the global filters, so tenant and unidades are explicit.
        db.Database.SetCommandTimeout(PrazoConsulta);
        var horas = await db.Database.SqlQuery<Hora>($"""
            SELECT "UnidadeId",
                   date_part('hour', "IniciadoEm" AT TIME ZONE {FusoHorario})::int AS "Valor",
                   count(*)::int AS "Quantidade"
              FROM paf."Atendimentos"
             WHERE "TenantId" = {tenantId}
               AND "UnidadeId" = ANY({unidadesIds})
               AND "DataOperacional" BETWEEN {filtro.DataInicial} AND {filtro.DataFinal}
               AND "IniciadoEm" IS NOT NULL
               AND "Estado" <> 'Cancelado'
               AND (cardinality({filas}) = 0 OR "FilaId" = ANY({filas}))
               AND (cardinality({prioridades}) = 0 OR "PrioridadeId" = ANY({prioridades}))
             GROUP BY 1, 2
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        var unidades = await NomesDasUnidadesAsync(db, filtro, cancellationToken).ConfigureAwait(false);

        var ordenadas = horas
            .OrderBy(h => NomeDaUnidade(unidades, h.UnidadeId), StringComparer.CurrentCulture)
            .ThenBy(h => h.Valor)
            .ToList();
        return Tabela(
            Tipo,
            [ColunaUnidade, ColunaHora, ColunaAtendimentos],
            ordenadas,
            h => h.UnidadeId,
            h => RotuloDaHora(h.Valor),
            unidades,
            grupo => [Numero(grupo.Sum(h => h.Quantidade))]);
    }

    internal sealed record Hora(Guid UnidadeId, int Valor, int Quantidade);
}

/// <summary>
/// Type 6 per fila (RF07): não efetuados = neither started nor cancelled at the moment of the query (today this
/// includes who still waits or was just called); cancelados = every cancelled one, started or not. A no-show is
/// handled by cancelling (seção 4), so it lands in cancelados.
/// </summary>
public sealed class ConsultaAtendimentosNaoEfetuadosOuCancelados(PafDbContext db) : IConsultaEstatistica
{
    public static readonly ColunaEstatisticaDto ColunaNaoEfetuados = new("naoEfetuados", "Não efetuados", TipoColunaEstatistica.Inteiro);
    public static readonly ColunaEstatisticaDto ColunaCancelados = new("cancelados", "Cancelados", TipoColunaEstatistica.Inteiro);

    public TipoEstatistica Tipo => TipoEstatistica.AtendimentosNaoEfetuadosOuCancelados;

    public async Task<ResultadoEstatisticaDto> ConsultarAsync(FiltroEstatistica filtro, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        var contagens = await DoPeriodo(db, filtro)
            .Where(a => a.Estado == EstadoAtendimento.Cancelado || a.IniciadoEm == null)
            .GroupBy(a => new { a.UnidadeId, a.FilaId })
            .Select(g => new Contagem(
                g.Key.UnidadeId,
                g.Key.FilaId,
                g.Count(a => a.Estado != EstadoAtendimento.Cancelado),
                g.Count(a => a.Estado == EstadoAtendimento.Cancelado)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var unidades = await NomesDasUnidadesAsync(db, filtro, cancellationToken).ConfigureAwait(false);
        var filas = await NomesDasFilasAsync(db, contagens.Select(c => c.FilaId), cancellationToken).ConfigureAwait(false);

        var ordenadas = contagens
            .OrderBy(c => NomeDaUnidade(unidades, c.UnidadeId), StringComparer.CurrentCulture)
            .ThenBy(c => filas[c.FilaId], StringComparer.CurrentCulture)
            .ToList();
        return Tabela(
            Tipo,
            [ColunaUnidade, ColunaFila, ColunaNaoEfetuados, ColunaCancelados],
            ordenadas,
            c => c.UnidadeId,
            c => filas[c.FilaId],
            unidades,
            grupo => [Numero(grupo.Sum(c => c.NaoEfetuados)), Numero(grupo.Sum(c => c.Cancelados))]);
    }

    private sealed record Contagem(Guid UnidadeId, Guid FilaId, int NaoEfetuados, int Cancelados);
}
