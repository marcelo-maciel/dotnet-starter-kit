using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estatistica;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estatistica.ConsultarEstatistica;

/// <summary>
/// RF07 entry point: resolves the population from the caller's effective scope (C01), never from the current
/// unit, so the same query serves the filial admin, the regional manager and the empresa admin. Every filter id
/// outside the scope is a 403 and every unknown id a 400: a filter is never narrowed silently.
/// </summary>
public sealed class ConsultarEstatisticaQueryHandler(
    PafDbContext db,
    EscopoUsuario escopo,
    IUserPermissionService permissoes,
    IEnumerable<IConsultaEstatistica> consultas)
    : IQueryHandler<ConsultarEstatisticaQuery, ResultadoEstatisticaDto>
{
    public const string MensagemSemPermissaoPorAtendente = "Sem permissão para a estatística por atendente.";

    private static readonly string[] SoOFiltroDeUnidade = [PafQueryFilters.Unidade];

    public async ValueTask<ResultadoEstatisticaDto> Handle(ConsultarEstatisticaQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var tipo = query.Tipo ?? throw new ArgumentException("Tipo ausente depois da validação.", nameof(query));
        var usuarioId = escopo.ObterUsuarioId();

        // Type 4 is personal data of employees (D10): a permission of its own on top of Estatisticas.View.
        if (tipo == TipoEstatistica.AtendimentosPorAtendente
            && !await permissoes.HasPermissionAsync(usuarioId.ToString(), PafPermissions.Estatisticas.ViewPorAtendente, cancellationToken).ConfigureAwait(false))
        {
            throw new ForbiddenException(MensagemSemPermissaoPorAtendente);
        }

        var escopoEfetivo = await escopo.EscopoEfetivoAsync(usuarioId, cancellationToken).ConfigureAwait(false);
        var unidades = UnidadesConsultadas(query.Unidades ?? [], escopoEfetivo);

        var filas = (query.Filas ?? []).Distinct().ToArray();
        var pertencaFilas = await db.Filas.IgnoreQueryFilters(SoOFiltroDeUnidade).AsNoTracking()
            .Where(f => filas.Contains(f.Id))
            .Select(f => new Pertenca(f.Id, f.UnidadeId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ExigirDentroDasUnidades(filas, pertencaFilas, "Fila", escopoEfetivo, unidades);

        var prioridades = (query.Prioridades ?? []).Distinct().ToArray();
        var pertencaPrioridades = await db.Prioridades.IgnoreQueryFilters(SoOFiltroDeUnidade).AsNoTracking()
            .Where(p => prioridades.Contains(p.Id))
            .Select(p => new Pertenca(p.Id, p.UnidadeId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ExigirDentroDasUnidades(prioridades, pertencaPrioridades, "Prioridade", escopoEfetivo, unidades);

        var filtro = new FiltroEstatistica(
            tipo,
            query.DataInicial ?? throw new ArgumentException("Data inicial ausente depois da validação.", nameof(query)),
            query.DataFinal ?? throw new ArgumentException("Data final ausente depois da validação.", nameof(query)),
            query.Agrupamento,
            unidades,
            filas,
            prioridades,
            query.SomenteEstacoesComAtendimento == true);

        // Single, not First: two registrations for one type is a wiring bug that must fail loud.
        var consulta = consultas.SingleOrDefault(c => c.Tipo == tipo);

        // ponytail: no type is computed yet; E1 (types 1-6), E2 (7-8) and E3 (9-10) register their
        // IConsultaEstatistica in PafModule. Until then a valid query answers an empty table.
        return consulta is null
            ? new ResultadoEstatisticaDto(tipo, [], [], [])
            : await consulta.ConsultarAsync(filtro, cancellationToken).ConfigureAwait(false);
    }

    private static Guid[] UnidadesConsultadas(Guid[] pedidas, IReadOnlyList<Guid> escopoEfetivo)
    {
        if (pedidas.Length == 0)
        {
            return [.. escopoEfetivo];
        }

        if (pedidas.Any(u => !escopoEfetivo.Contains(u)))
        {
            throw new ForbiddenException(EscopoUsuario.MensagemForaDoEscopo);
        }

        return [.. escopoEfetivo.Where(pedidas.Contains)];
    }

    private static void ExigirDentroDasUnidades(
        Guid[] pedidos,
        List<Pertenca> encontrados,
        string nome,
        IReadOnlyList<Guid> escopoEfetivo,
        Guid[] unidades)
    {
        // Another tenant's id is hidden by the tenant filter and lands here as unknown, never as out of scope.
        if (encontrados.Count != pedidos.Length)
        {
            throw new CustomException($"{nome} inexistente.", (IEnumerable<string>?)null, HttpStatusCode.BadRequest);
        }

        if (encontrados.Any(e => !escopoEfetivo.Contains(e.UnidadeId)))
        {
            throw new ForbiddenException(EscopoUsuario.MensagemForaDoEscopo);
        }

        if (encontrados.Any(e => !unidades.Contains(e.UnidadeId)))
        {
            throw new CustomException($"{nome} de unidade fora das unidades consultadas.", (IEnumerable<string>?)null, HttpStatusCode.BadRequest);
        }
    }

    private sealed record Pertenca(Guid Id, Guid UnidadeId);
}
