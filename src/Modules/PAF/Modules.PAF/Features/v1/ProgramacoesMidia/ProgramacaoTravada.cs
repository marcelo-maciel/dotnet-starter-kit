using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Midias;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.ProgramacoesMidia;

/// <summary>The mídia a programação plays, as read under its share lock.</summary>
internal sealed record MidiaDaProgramacao(Guid Id, string Descricao, int DuracaoSegundos, bool Ativa);

/// <summary>Row locks, version check and RF10 write rules shared by every write on a programação.</summary>
internal static class ProgramacaoTravada
{
    public const string MensagemVersao = "Esta programação foi alterada por outra pessoa. Recarregue e tente de novo.";

    public const string MensagemMidia = "Escolha uma mídia ativa, com o arquivo já enviado, desta unidade.";

    /// <summary>
    /// <c>FOR UPDATE</c> on the programação row, then the tracked read: two writes on the same programação run one after
    /// the other, and the second one sees the first one's <c>Versao</c>. Needs the caller's open transaction.
    /// </summary>
    public static async Task<ProgramacaoMidia> TravarAsync(PafDbContext db, string tenantId, Guid unidadeId, Guid id, CancellationToken ct)
    {
        ExigirTransacao(db);
        var travadas = await db.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM paf."ProgramacoesMidia"
             WHERE "TenantId" = {tenantId} AND "UnidadeId" = {unidadeId} AND "Id" = {id}
               FOR UPDATE
            """).ToListAsync(ct).ConfigureAwait(false);
        if (travadas.Count == 0)
        {
            throw NaoEncontrada(id);
        }

        return await db.ProgramacoesMidia.FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false)
            ?? throw NaoEncontrada(id);
    }

    /// <summary>
    /// Only an active mídia of the current unidade whose file was confirmed can be programmed (RF10). <c>FOR SHARE</c>
    /// holds the mídia until commit, so a concurrent disable or duration edit waits instead of slipping between this
    /// check and the write. Another unidade's or tenant's mídia, an inactive one and an unconfirmed one answer the same
    /// 400, so the answer never tells whether the id exists elsewhere.
    /// </summary>
    public static async Task<MidiaDaProgramacao> ExigirMidiaProgramavelAsync(
        PafDbContext db, string tenantId, Guid unidadeId, Guid midiaId, CancellationToken ct)
    {
        ExigirTransacao(db);
        var travadas = await db.Database.SqlQuery<Guid>($"""
            SELECT m."Id" AS "Value" FROM paf."Midias" m
              JOIN paf."EnviosMidia" e ON e."TenantId" = {tenantId} AND e."Id" = m."EnvioId"
             WHERE m."TenantId" = {tenantId} AND m."UnidadeId" = {unidadeId} AND m."Id" = {midiaId}
               AND m."Ativa" AND e."ConfirmadoEm" IS NOT NULL
               FOR SHARE OF m
            """).ToListAsync(ct).ConfigureAwait(false);
        if (travadas.Count == 0)
        {
            throw new CustomException(MensagemMidia, [], HttpStatusCode.BadRequest);
        }

        return await LerMidiaAsync(db, midiaId, ct).ConfigureAwait(false);
    }

    /// <summary>D13: refused at the cadastro, with the computed end in the message so the form can show it.</summary>
    public static void ExigirFimAteAMeiaNoite(TimeOnly horaInicial, MidiaDaProgramacao midia, int repeticoes)
    {
        ArgumentNullException.ThrowIfNull(midia);
        if (AgendaMidia.TerminaAteAMeiaNoite(horaInicial, midia.DuracaoSegundos, repeticoes))
        {
            return;
        }

        var excesso = TimeSpan.FromSeconds(AgendaMidia.HoraFinalSegundos(horaInicial, midia.DuracaoSegundos, repeticoes) - AgendaMidia.SegundosNoDia);
        throw new CustomException(
            $"A programação terminaria depois da meia-noite ({excesso:hh\\:mm\\:ss} além dela). Reduza as repetições ou antecipe a hora inicial.",
            [],
            HttpStatusCode.BadRequest);
    }

    public static void ExigirVersao(ProgramacaoMidia programacao, int versaoLida)
    {
        ArgumentNullException.ThrowIfNull(programacao);
        if (programacao.Versao != versaoLida)
        {
            throw new CustomException(MensagemVersao, [], HttpStatusCode.Conflict);
        }
    }

    public static async Task<MidiaDaProgramacao> LerMidiaAsync(PafDbContext db, Guid midiaId, CancellationToken ct) =>
        await db.Midias.AsNoTracking()
            .Where(m => m.Id == midiaId)
            .Select(m => new MidiaDaProgramacao(m.Id, m.Descricao, m.DuracaoSegundos, m.Ativa))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false)
        ?? throw new CustomException(MensagemMidia, [], HttpStatusCode.BadRequest);

    public static NotFoundException NaoEncontrada(Guid id) => new($"Programação {id} não encontrada nesta unidade.");

    public static ProgramacaoMidiaDto ParaDto(ProgramacaoMidia p, MidiaDaProgramacao m)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(m);
        return new ProgramacaoMidiaDto(
            p.Id, p.UnidadeId, p.MidiaId, m.Descricao, m.Ativa, p.Nome, p.DataInicial, p.DataFinal, p.HoraInicial,
            p.Repeticoes, (int)p.Dias, p.Ativa, p.Versao, m.DuracaoSegundos,
            AgendaMidia.TempoTotalSegundos(m.DuracaoSegundos, p.Repeticoes),
            AgendaMidia.HoraFinalSegundos(p.HoraInicial, m.DuracaoSegundos, p.Repeticoes));
    }

    private static void ExigirTransacao(PafDbContext db)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("ProgramacaoTravada needs an open transaction.");
        }
    }
}
