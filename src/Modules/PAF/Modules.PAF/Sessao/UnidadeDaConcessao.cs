using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// Unidade corrente da impersonação (seção 9, ARQ "estado de impersonação também é recriado" e modo degradado):
/// a chave da concessão no Valkey; sem ela, recriada pela estação do vínculo ativo da concessão, senão pela unidade
/// corrente da sessão viva do impersonado, senão pela principal dele. Sem Valkey, a mesma ordem pelo PostgreSQL.
/// Toda fonte só vale dentro do escopo efetivo do impersonado; nenhuma ocupa licença nem toca na sessão do usuário.
/// </summary>
public sealed partial class UnidadeDaConcessao(
    PafDbContext db,
    EstadoSessao estado,
    EscopoUsuario escopo,
    ILogger<UnidadeDaConcessao> logger)
{
    /// <summary>Nulo só com escopo vazio: o filtro de unidade fica fechado.</summary>
    public async Task<Guid?> ResolverAsync(ConcessaoImpersonacao concessao, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(concessao);
        var escopoEfetivo = await escopo.EscopoEfetivoAsync(concessao.UsuarioId, ct).ConfigureAwait(false);
        if (escopoEfetivo.Count == 0)
        {
            return null;
        }

        if (await estado.LerUnidadeDaConcessaoAsync(concessao.TenantId, concessao.ConcessaoId, ct).ConfigureAwait(false) is { } guardada)
        {
            if (escopoEfetivo.Contains(guardada))
            {
                return guardada;
            }

            LogForaDoEscopo(logger, concessao.ConcessaoId, guardada);
        }

        var recriada = NoEscopo(escopoEfetivo, await DoVinculoAsync(concessao, ct).ConfigureAwait(false))
            ?? NoEscopo(escopoEfetivo, await DaSessaoVivaAsync(concessao, ct).ConfigureAwait(false))
            ?? escopoEfetivo[0];

        // Best effort: without Valkey the next request recreates it by the same order (modo degradado).
        await estado.DefinirUnidadeDaConcessaoAsync(concessao.TenantId, concessao.ConcessaoId, recriada, concessao.ExpiraEm, ct)
            .ConfigureAwait(false);
        return recriada;
    }

    private static Guid? NoEscopo(IReadOnlyList<Guid> escopoEfetivo, Guid? unidadeId) =>
        unidadeId is { } id && escopoEfetivo.Contains(id) ? id : null;

    // The support may have switched unit and selected a guichê there before the state was lost: the link wins.
    // Raw SQL because Estacoes is unit-filtered and the current unit is what is being resolved.
    private Task<Guid?> DoVinculoAsync(ConcessaoImpersonacao concessao, CancellationToken ct) =>
        db.Database.SqlQuery<Guid?>($"""
            SELECT e."UnidadeId" AS "Value"
              FROM paf."SessoesEstacao" s
              JOIN paf."Estacoes" e ON e."Id" = s."EstacaoId" AND e."TenantId" = {concessao.TenantId}
             WHERE s."TenantId" = {concessao.TenantId} AND s."ConcessaoId" = {concessao.ConcessaoId}
               AND s."LiberadoEm" IS NULL
            """).FirstOrDefaultAsync(ct);

    // The freshest live session of the impersonated user: its Valkey state (the unit it switched to), else the
    // unit of its licence row.
    private async Task<Guid?> DaSessaoVivaAsync(ConcessaoImpersonacao concessao, CancellationToken ct)
    {
        var viva = await db.Database.SqlQuery<SessaoViva>($"""
            SELECT "SessaoId", "UnidadeId"
              FROM paf."AtividadesSessao"
             WHERE "TenantId" = {concessao.TenantId} AND "UsuarioId" = {concessao.UsuarioId}
               AND "UltimaAtividadeEm" > now() - make_interval(mins => {Licencas.PrazoMarcaMinutos})
             ORDER BY "UltimaAtividadeEm" DESC
             LIMIT 1
            """).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (viva is null)
        {
            return null;
        }

        return await estado.LerUnidadeAsync(concessao.TenantId, viva.SessaoId, ct).ConfigureAwait(false) ?? viva.UnidadeId;
    }

    private sealed record SessaoViva(Guid SessaoId, Guid UnidadeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[PAF] concessao {ConcessaoId}: unidade guardada {UnidadeId} fora do escopo do impersonado; recriada")]
    private static partial void LogForaDoEscopo(ILogger logger, Guid concessaoId, Guid unidadeId);
}
