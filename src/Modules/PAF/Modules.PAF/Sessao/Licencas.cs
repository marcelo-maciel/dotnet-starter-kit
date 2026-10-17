using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// Licença da sessão (RF22), primeira fatia: sem cascata, paga a unidade principal do usuário, e sem saldo nela
/// o login é recusado. A contagem é sempre a de <see cref="AtividadeSessao"/> no PostgreSQL, serializada por
/// bloqueio consultivo da unidade (seção 9, modo degradado), com ou sem Valkey.
/// </summary>
public sealed class Licencas(PafDbContext db, IUnidadeCorrente unidadeCorrente, ILogger<Licencas> logger)
{
    // ponytail: initial values of seção 9 as constants; move to options when measurement asks for others.
    public const int PrazoMarcaMinutos = 10;
    public const int IntervaloMarcaMinutos = 1;

    public const string MensagemSemUnidadeAtiva =
        "A unidade do seu acesso está inativa. Procure o administrador da empresa.";
    public const string MensagemSemContrato =
        "A unidade do seu acesso não tem contrato ativo. Procure o administrador da empresa.";
    public const string MensagemLimite =
        "O limite de licenças da unidade foi atingido. Aguarde outro usuário sair ou procure o administrador.";

    /// <summary>
    /// Abre a transação da ocupação e confere o saldo sob o bloqueio da unidade. Nulo quando o usuário não tem
    /// unidade no PAF: ele não opera o PAF, e o filtro de unidade continua fechado para ele.
    /// </summary>
    public async Task<ReservaLicenca?> ReservarAsync(string tenantId, Guid usuarioId, CancellationToken ct)
    {
        var principal = await db.UsuariosUnidade.AsNoTracking()
            .Where(u => u.UsuarioId == usuarioId)
            .OrderBy(u => u.Ordem)
            .Select(u => (Guid?)u.UnidadeId)
            .FirstOrDefaultAsync(ct);
        if (principal is not { } unidadeId)
        {
            return null;
        }

        var transacao = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await ConferirSaldoAsync(tenantId, usuarioId, unidadeId, ct);
        }
        catch
        {
            await transacao.DisposeAsync();
            throw;
        }

        unidadeCorrente.Definir(unidadeId);
        return new ReservaLicenca(db, transacao, tenantId, usuarioId, unidadeId);
    }

    private async Task ConferirSaldoAsync(string tenantId, Guid usuarioId, Guid unidadeId, CancellationToken ct)
    {
        // Two logins racing for the last seat of the same unit serialize here until one commits.
        var chave = $"paf-licenca:{tenantId}:{unidadeId}";
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({chave}, 0))", ct);

        var ativa = await db.Unidades.AsNoTracking()
            .Where(u => u.Id == unidadeId)
            .Select(u => u.Ativa)
            .FirstOrDefaultAsync(ct);
        if (!ativa)
        {
            throw new ForbiddenException(MensagemSemUnidadeAtiva);
        }

        // Validity is a calendar day in America/Sao_Paulo, taken from the PostgreSQL clock.
        var contrato = await db.Database.SqlQuery<ContratoVigente>($"""
            SELECT "LimiteSessoes", "Politica"
              FROM paf."ContratosUnidade"
             WHERE "TenantId" = {tenantId} AND "UnidadeId" = {unidadeId}
               AND "InicioVigencia" <= (now() AT TIME ZONE 'America/Sao_Paulo')::date
               AND ("FimVigencia" IS NULL OR "FimVigencia" >= (now() AT TIME ZONE 'America/Sao_Paulo')::date)
             ORDER BY "InicioVigencia" DESC
             LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (contrato is null)
        {
            throw new ForbiddenException(MensagemSemContrato);
        }

        // The user's own earlier sessions are released in the same act, so they do not count against him.
        var ocupadas = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value"
              FROM paf."AtividadesSessao"
             WHERE "TenantId" = {tenantId} AND "UnidadeId" = {unidadeId} AND "UsuarioId" <> {usuarioId}
               AND "UltimaAtividadeEm" > now() - make_interval(mins => {PrazoMarcaMinutos})
            """).SingleAsync(ct);
        if (ocupadas < contrato.LimiteSessoes)
        {
            return;
        }

        if (!string.Equals(contrato.Politica, nameof(PoliticaLimiteLicenca.UltrapassarComAviso), StringComparison.Ordinal))
        {
            throw new ForbiddenException(MensagemLimite);
        }

        logger.LogWarning(
            "Licença acima do limite contratado na unidade {UnidadeId} do tenant {TenantId}: {Ocupadas} de {Limite}",
            unidadeId, tenantId, ocupadas + 1, contrato.LimiteSessoes);
    }

    /// <summary>Unidade da licença quando a marca de sessão viva está dentro do prazo; nulo caso contrário.</summary>
    public Task<Guid?> UnidadeDaMarcaVivaAsync(string tenantId, Guid sessaoId, CancellationToken ct) =>
        db.Database.SqlQuery<Guid?>($"""
            SELECT "UnidadeId" AS "Value"
              FROM paf."AtividadesSessao"
             WHERE "TenantId" = {tenantId} AND "SessaoId" = {sessaoId}
               AND "UltimaAtividadeEm" > now() - make_interval(mins => {PrazoMarcaMinutos})
            """).FirstOrDefaultAsync(ct);

    public Task<bool> UsuarioDoPafAsync(Guid usuarioId, CancellationToken ct) =>
        db.UsuariosUnidade.AsNoTracking().AnyAsync(u => u.UsuarioId == usuarioId, ct);

    /// <summary>Renova a marca de sessão viva, no máximo uma escrita por intervalo; verdadeiro quando escreveu.</summary>
    public async Task<bool> RenovarMarcaAsync(string tenantId, Guid sessaoId, CancellationToken ct) =>
        await db.Database.ExecuteSqlAsync($"""
            UPDATE paf."AtividadesSessao" SET "UltimaAtividadeEm" = now()
             WHERE "TenantId" = {tenantId} AND "SessaoId" = {sessaoId}
               AND "UltimaAtividadeEm" < now() - make_interval(mins => {IntervaloMarcaMinutos})
            """, ct) > 0;

    /// <summary>Solta licença e guichê da sessão numa transação (logout).</summary>
    public async Task LiberarAsync(string tenantId, Guid sessaoId, CancellationToken ct)
    {
        await using var transacao = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE paf."SessoesEstacao" SET "LiberadoEm" = now()
             WHERE "TenantId" = {tenantId} AND "SessaoId" = {sessaoId} AND "LiberadoEm" IS NULL
            """, ct);
        await db.Database.ExecuteSqlAsync($"""
            DELETE FROM paf."AtividadesSessao" WHERE "TenantId" = {tenantId} AND "SessaoId" = {sessaoId}
            """, ct);
        await transacao.CommitAsync(ct);
    }

    private sealed record ContratoVigente(int LimiteSessoes, string Politica);
}
