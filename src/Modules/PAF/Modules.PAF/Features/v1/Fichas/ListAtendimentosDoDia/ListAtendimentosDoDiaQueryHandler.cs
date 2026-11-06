using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.Features.v1.Estacoes;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Fichas.ListAtendimentosDoDia;

/// <summary>Line of the RF12 list, materialized by EF from raw SQL.</summary>
internal sealed record LinhaDoDia(
    Guid AtendimentoId,
    Guid FichaId,
    string Letra,
    int Numero,
    Guid FilaId,
    string Fila,
    Guid PrioridadeId,
    string Prioridade,
    string Estado,
    int Versao,
    DateTimeOffset ChegadaEm,
    DateTimeOffset ElegivelDesde,
    long EsperaSegundos,
    Guid? EstacaoId,
    string? Guiche,
    string? ConfiguracaoGuiche,
    Guid? AtendimentoOrigemId);

/// <summary>
/// RF12 "fichas do dia" of the current unit. Raw SQL so the wait is computed by the PostgreSQL clock (seção 4.2),
/// which also means no global filter: tenant and unidade are explicit in every statement. The day is the
/// atendimento's <c>DataOperacional</c>, so a forward made after midnight lists under the new day and its origin under the old one.
/// </summary>
public sealed class ListAtendimentosDoDiaQueryHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor) : IQueryHandler<ListAtendimentosDoDiaQuery, FichasDoDiaDto>
{
    public const string MensagemFilaNaoEncontrada = "Fila não encontrada.";

    // ponytail: a cap instead of paging; a store's day is hundreds of atendimentos. Page by (CriadoEm, Id) when one passes it.
    public const int MaxAtendimentos = 1000;

    public async ValueTask<FichasDoDiaDto> Handle(ListAtendimentosDoDiaQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var unidadeId = unidadeCorrente.Exigir();
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();

        // Unit-filtered list: a fila of another unidade is not here, so asking for it is 404, never an empty list.
        var filas = await db.Filas.AsNoTracking()
            .OrderBy(f => f.NomeExibicao)
            .Select(f => new FilaDasFichasDto(f.Id, f.NomeExibicao, f.Ativa))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (query.FilaId is { } filaPedida && filas.TrueForAll(f => f.Id != filaPedida))
        {
            throw new NotFoundException(MensagemFilaNaoEncontrada);
        }

        var data = query.Data ?? await PafRelogio.HojeAsync(db, cancellationToken).ConfigureAwait(false);
        var estado = query.Estado is null ? (EstadoAtendimento?)null : Enum.Parse<EstadoAtendimento>(query.Estado);

        var linhas = await db.Database
            .SqlQuery<LinhaDoDia>(ConsultaLinhas(tenantId, unidadeId, data, query.FilaId, estado))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var aguardando = await db.Database
            .SqlQuery<int>(ConsultaTotalAguardando(tenantId, unidadeId, data, query.FilaId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var truncada = linhas.Count > MaxAtendimentos;
        var atendimentos = linhas.Take(MaxAtendimentos).Select(Dto).ToList();
        return new FichasDoDiaDto(data, aguardando.Single(), truncada, atendimentos, filas);
    }

    private static AtendimentoDoDiaDto Dto(LinhaDoDia l)
    {
        var (numeroGuiche, nomeExibicaoGuiche) = ConfiguracaoDaEstacao.IdentidadeDoGuiche(l.ConfiguracaoGuiche);
        return new AtendimentoDoDiaDto(l.AtendimentoId, l.FichaId, OperacaoGuiche.Senha(l.Letra, l.Numero), l.FilaId, l.Fila,
            l.PrioridadeId, l.Prioridade, l.Estado, l.Versao, l.ChegadaEm, l.ElegivelDesde, l.EsperaSegundos,
            l.EstacaoId, l.Guiche, numeroGuiche, nomeExibicaoGuiche, l.AtendimentoOrigemId is not null);
    }

    private static readonly FormattableString SemCondicao = $"";

    // One row past the cap tells the screen the list was cut.
    internal static FormattableString ConsultaLinhas(string tenantId, Guid unidadeId, DateOnly data, Guid? filaId, EstadoAtendimento? estado) =>
        ChamadaDeFicha.Concatenar(
            $"""
            SELECT a."Id" AS "AtendimentoId", a."FichaId", f."Letra", f."Numero", a."FilaId", fi."NomeExibicao" AS "Fila",
                   a."PrioridadeId", p."Nome" AS "Prioridade", a."Estado", a."Versao", f."CriadoEm" AS "ChegadaEm", a."ElegivelDesde",
                   GREATEST(0, floor(EXTRACT(EPOCH FROM COALESCE(a."ChamadoEm", a."CanceladoEm", now()) - a."ElegivelDesde")))::bigint AS "EsperaSegundos",
                   a."EstacaoId", e."Nome" AS "Guiche", e."Configuracao"::text AS "ConfiguracaoGuiche", a."AtendimentoOrigemId"
              FROM paf."Atendimentos" a
              JOIN paf."Fichas" f ON f."TenantId" = {tenantId} AND f."Id" = a."FichaId"
              JOIN paf."Filas" fi ON fi."TenantId" = {tenantId} AND fi."Id" = a."FilaId"
              JOIN paf."Prioridades" p ON p."TenantId" = {tenantId} AND p."Id" = a."PrioridadeId"
              LEFT JOIN paf."Estacoes" e ON e."TenantId" = {tenantId} AND e."Id" = a."EstacaoId"
             WHERE a."TenantId" = {tenantId} AND a."UnidadeId" = {unidadeId} AND a."DataOperacional" = {data}
            """,
            filaId is { } fila ? (FormattableString)$"""   AND a."FilaId" = {fila}""" : SemCondicao,
            estado is { } e ? (FormattableString)$"""   AND a."Estado" = {e.ToString()}""" : SemCondicao,
            $"""
             ORDER BY a."CriadoEm" DESC, a."Id" DESC
             LIMIT {MaxAtendimentos + 1}
            """);

    internal static FormattableString ConsultaTotalAguardando(string tenantId, Guid unidadeId, DateOnly data, Guid? filaId) =>
        ChamadaDeFicha.Concatenar(
            $"""
            SELECT count(*)::int AS "Value" FROM paf."Atendimentos" a
             WHERE a."TenantId" = {tenantId} AND a."UnidadeId" = {unidadeId} AND a."DataOperacional" = {data} AND a."Estado" = 'Aguardando'
            """,
            filaId is { } fila ? (FormattableString)$"""   AND a."FilaId" = {fila}""" : SemCondicao);
}
