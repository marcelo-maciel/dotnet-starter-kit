using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Midias;
using FSH.Modules.PAF.Sessao;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.Features.v1.Midias;

/// <summary>
/// Confirmation of an envio, shared by mídia creation and file swap (RF03). The HEAD runs before any transaction, so no
/// row lock waits on the store; the stamps are raw <c>now()</c> UPDATEs (PostgreSQL clock), conditional on the envio
/// still being open, which is what serializes two confirmations of the same envio.
/// </summary>
public sealed partial class ConfirmacaoEnvioMidia(
    PafDbContext db,
    ArmazenamentoMidia armazenamento,
    AtosImpersonacao atos,
    ILogger<ConfirmacaoEnvioMidia> logger)
{
    public const string MensagemNaoRecebido = "O arquivo deste envio ainda não chegou ao armazenamento. Conclua o envio e confirme de novo.";
    public const string MensagemRecusado = "Este envio foi recusado. Peça um envio novo.";
    public const string MensagemJaUsado = "Este envio já foi usado. Peça um envio novo.";

    public async Task<EnvioMidia> LerAsync(Guid envioId, CancellationToken ct) =>
        await db.EnviosMidia.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == envioId, ct)
            .ConfigureAwait(false)
        ?? throw new NotFoundException($"Envio {envioId} não encontrado nesta unidade.");

    /// <summary>
    /// Refuses an envio that can no longer be confirmed, then checks the stored object. A refused object marks the envio
    /// (<c>RecusadoEm</c>, committed on its own) so the cleanup removes it after the lock, and answers 400.
    /// </summary>
    public async Task ConferirAsync(EnvioMidia envio, string tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envio);
        ExigirAberto(envio);

        var (desfecho, objeto) = await armazenamento.ConferirAsync(envio, ct).ConfigureAwait(false);
        switch (desfecho)
        {
            case DesfechoConferencia.Aceito:
                return;
            case DesfechoConferencia.NaoRecebido:
                throw new CustomException(MensagemNaoRecebido, [], HttpStatusCode.Conflict);
        }

        long teto = armazenamento.TetoBytes(envio.Tipo);
        await RecusarAsync(envio, tenantId, ct).ConfigureAwait(false);
        LogEnvioRecusado(envio.Id, desfecho, envio.Tipo, objeto?.SizeBytes ?? 0, teto);
        throw new CustomException(MensagemDaRecusa(desfecho, envio.Tipo, teto), [], HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Inside the caller's transaction: stamps <c>ConfirmadoEm</c> only while the envio is neither confirmed nor refused.
    /// False = another request confirmed or refused it first (its commit is visible once the row lock is released).
    /// </summary>
    public async Task<bool> MarcarConfirmadoAsync(EnvioMidia envio, string tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envio);
        int linhas = await db.Database.ExecuteSqlAsync($"""
            UPDATE paf."EnviosMidia" SET "ConfirmadoEm" = now()
             WHERE "TenantId" = {tenantId} AND "UnidadeId" = {envio.UnidadeId} AND "Id" = {envio.Id}
               AND "ConfirmadoEm" IS NULL AND "RecusadoEm" IS NULL
            """, ct).ConfigureAwait(false);
        await atos.GravarAsync(atendimentoId: null, ct).ConfigureAwait(false);
        return linhas == 1;
    }

    /// <summary>The envio's own state after losing <see cref="MarcarConfirmadoAsync"/>: refused, or used by another write.</summary>
    public static void ExigirAberto(EnvioMidia envio)
    {
        ArgumentNullException.ThrowIfNull(envio);
        if (envio.RecusadoEm is not null)
        {
            throw new CustomException(MensagemRecusado, [], HttpStatusCode.BadRequest);
        }

        if (envio.ConfirmadoEm is not null)
        {
            throw new CustomException(MensagemJaUsado, [], HttpStatusCode.Conflict);
        }
    }

    public static string MensagemDaRecusa(DesfechoConferencia desfecho, TipoMidia tipo, long tetoBytes) => desfecho switch
    {
        DesfechoConferencia.RecusadoTeto =>
            $"Arquivo acima do tamanho máximo para {tipo}: {tetoBytes / (1024 * 1024)} MiB. Peça um envio novo com um arquivo menor.",
        DesfechoConferencia.RecusadoVazio => "Arquivo vazio. Peça um envio novo.",
        _ => $"O tipo do arquivo guardado não confere com o do envio. Formatos aceitos: {FormatosMidia.ListaAceitos}.",
    };

    private async Task RecusarAsync(EnvioMidia envio, string tenantId, CancellationToken ct)
    {
        await using var transacao = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE paf."EnviosMidia" SET "RecusadoEm" = now()
             WHERE "TenantId" = {tenantId} AND "UnidadeId" = {envio.UnidadeId} AND "Id" = {envio.Id}
               AND "ConfirmadoEm" IS NULL AND "RecusadoEm" IS NULL
            """, ct).ConfigureAwait(false);
        await atos.GravarAsync(atendimentoId: null, ct).ConfigureAwait(false);
        await transacao.CommitAsync(ct).ConfigureAwait(false);
    }

    // No file name and no key in the log (RNF06): the envio id finds the row.
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Envio de mídia {EnvioId} recusado na confirmação: {Desfecho}, tipo {Tipo}, {TamanhoBytes} bytes, teto {TetoBytes}")]
    private partial void LogEnvioRecusado(Guid envioId, DesfechoConferencia desfecho, TipoMidia tipo, long tamanhoBytes, long tetoBytes);
}
