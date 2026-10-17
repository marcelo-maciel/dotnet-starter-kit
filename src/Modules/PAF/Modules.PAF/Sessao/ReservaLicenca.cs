using FSH.Modules.PAF.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// Vaga conferida e ainda não gravada, dentro da transação que segura o bloqueio da unidade. Descartada sem
/// <see cref="ConfirmarAsync"/>, a transação volta e nada fica ocupado.
/// </summary>
public sealed class ReservaLicenca(
    PafDbContext db,
    IDbContextTransaction transacao,
    string tenantId,
    Guid usuarioId,
    Guid unidadeId) : IAsyncDisposable
{
    public Guid UnidadeId => unidadeId;

    /// <summary>
    /// Ocupa a licença para a sessão e solta, no mesmo ato, licenças e guichês das sessões anteriores do usuário
    /// e o guichê que a própria sessão ainda tiver, porque a licença dela já tinha sido solta.
    /// </summary>
    public async Task ConfirmarAsync(Guid sessaoId, IReadOnlyCollection<Guid> sessoesAnteriores, CancellationToken ct)
    {
        var soltas = sessoesAnteriores.Append(sessaoId).ToArray();
        await db.Database.ExecuteSqlAsync($"""
            UPDATE paf."SessoesEstacao" SET "LiberadoEm" = now()
             WHERE "TenantId" = {tenantId} AND "SessaoId" = ANY({soltas}) AND "LiberadoEm" IS NULL
            """, ct);
        await db.Database.ExecuteSqlAsync($"""
            DELETE FROM paf."AtividadesSessao"
             WHERE "TenantId" = {tenantId} AND "UsuarioId" = {usuarioId} AND "SessaoId" <> {sessaoId}
            """, ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO paf."AtividadesSessao" ("SessaoId", "UsuarioId", "UnidadeId", "UltimaAtividadeEm", "TenantId")
            VALUES ({sessaoId}, {usuarioId}, {unidadeId}, now(), {tenantId})
            ON CONFLICT ("SessaoId") DO UPDATE SET "UnidadeId" = EXCLUDED."UnidadeId", "UltimaAtividadeEm" = now()
            """, ct);
        await transacao.CommitAsync(ct);
    }

    public ValueTask DisposeAsync() => transacao.DisposeAsync();
}
