using System.Diagnostics;
using System.Globalization;
using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FSH.Modules.PAF.Features.v1.Atendimentos;

/// <summary>Messages of the guichê operations; the RF16 text is the legacy one, kept verbatim.</summary>
public static class MensagensGuiche
{
    public const string MensagemSemGuiche = "Selecione um guichê antes de operar.";
    public const string MensagemNaoEncontrado = "Atendimento não encontrado neste guichê.";
    public const string MensagemNaoChamado = "A senha não está chamada neste guichê.";
    public const string MensagemNaoAtendendo = "A estação não está atendendo.";
    public const string MensagemDeOutroUsuario = "Este atendimento foi chamado por outro usuário e só ele pode operá-lo.";
}

/// <summary>The guichê of the post's owner (session, or impersonation grant since S11) and the station it holds.</summary>
internal sealed record Guiche(DonoPosto Dono, Estacao Estacao)
{
    public string TenantId => Dono.TenantId;

    /// <summary>Who the atendimento is recorded for: the impersonated user (<c>sub</c>) under impersonation.</summary>
    public Guid UsuarioId => Dono.UsuarioId;
}

/// <summary>Atendimento re-read after its ficha's rows were locked; the state check runs on this, never on a read from before the lock.</summary>
internal sealed record AtendimentoTravado(
    Guid Id,
    Guid UnidadeId,
    Guid FichaId,
    Guid FilaId,
    Guid PrioridadeId,
    Guid? EstacaoId,
    EstadoAtendimento Estado,
    string Letra,
    int Numero,
    DateTimeOffset CriadoEm,
    DateOnly DataElegibilidade,
    Guid? UsuarioId);

/// <summary>Result of the RF12 cancel: the new version and the notices to write before the commit and send after it.</summary>
internal sealed record AtendimentoCancelado(Guid AtendimentoId, int Versao, IReadOnlyList<AvisoPaf> Avisos);

/// <summary>
/// Shared steps of the guichê operations (seção 8): resolve the guichê from the session, lock every
/// atendimento row of the ficha in ascending id order, re-validate, then write with the PostgreSQL clock.
/// </summary>
internal static class OperacaoGuiche
{
    public static async Task<Guiche> ExigirAsync(PafDbContext db, DonoPosto dono, CancellationToken ct)
    {
        var vinculo = await VinculosGuiche.AtivoDoDonoAsync(db, dono, ct).ConfigureAwait(false)
            ?? throw Conflito(MensagensGuiche.MensagemSemGuiche);

        // Unit-filtered: a guichê of a unit the session left is not operable from here.
        var estacao = await db.Estacoes.AsNoTracking().FirstOrDefaultAsync(e => e.Id == vinculo.EstacaoId, ct).ConfigureAwait(false)
            ?? throw Conflito(MensagensGuiche.MensagemSemGuiche);
        return new Guiche(dono, estacao);
    }

    /// <summary>
    /// The guichê's entry to <see cref="TravarFichaAsync"/>: an atendimento this guichê does not hold answers 404
    /// before any lock, so a guichê never waits on a ficha that is not its own. Every guichê write on an existing
    /// atendimento enters here, so this is where the impersonation act (D28, S11) is written, inside the caller's
    /// transaction: a refusal after it rolls the act back with the rest.
    /// </summary>
    public static async Task<AtendimentoTravado> TravarAsync(PafDbContext db, AtosImpersonacao atos, Guiche guiche, Guid atendimentoId, CancellationToken ct)
    {
        var doGuiche = await db.Atendimentos.AsNoTracking()
            .AnyAsync(a => a.Id == atendimentoId && a.EstacaoId == guiche.Estacao.Id, ct)
            .ConfigureAwait(false);
        if (!doGuiche)
        {
            throw new NotFoundException(MensagensGuiche.MensagemNaoEncontrado);
        }

        var travado = await TravarFichaAsync(db, guiche.TenantId, atendimentoId, ct).ConfigureAwait(false);

        // RF04: a pendência left on the guichê stays with the user who called it, so whoever holds the link now
        // must not end it in their name (it would land in the wrong user's statistics, RF07). Checked on the row
        // re-read under the lock; an atendimento that left this guichê meanwhile falls to the state check instead.
        // guiche.UsuarioId is the impersonated user under impersonation, so the support acts as that user.
        if (travado.EstacaoId == guiche.Estacao.Id && travado.UsuarioId != guiche.UsuarioId)
        {
            throw Conflito(MensagensGuiche.MensagemDeOutroUsuario);
        }

        await atos.GravarAsync(travado.Id, ct).ConfigureAwait(false);
        return travado;
    }

    /// <summary>
    /// Locks all atendimentos of the ficha (waiting, ascending id: two operations on one ficha ask in the same
    /// order whichever atendimento they entered by, so they never deadlock) and re-reads the target under the
    /// lock. Needs no guichê: the RF12 paths outside it enter here too. An atendimento of another unit is not
    /// found, because the first read goes through the unit filter.
    /// </summary>
    public static async Task<AtendimentoTravado> TravarFichaAsync(PafDbContext db, string tenantId, Guid atendimentoId, CancellationToken ct)
    {
        ExigirTransacao(db);
        var fichaId = await db.Atendimentos.AsNoTracking()
            .Where(a => a.Id == atendimentoId)
            .Select(a => (Guid?)a.FichaId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException(MensagensGuiche.MensagemNaoEncontrado);

        // Raw SQL skips the global filters: tenant is explicit, and the ficha id came from a filtered read.
        await db.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM paf."Atendimentos"
             WHERE "TenantId" = {tenantId} AND "FichaId" = {fichaId}
             ORDER BY "Id"
               FOR UPDATE
            """).ToListAsync(ct).ConfigureAwait(false);

        return await (
                from a in db.Atendimentos.AsNoTracking()
                join f in db.Fichas.AsNoTracking() on a.FichaId equals f.Id
                where a.Id == atendimentoId
                select new AtendimentoTravado(
                    a.Id, a.UnidadeId, a.FichaId, a.FilaId, a.PrioridadeId, a.EstacaoId, a.Estado, f.Letra, f.Numero,
                    a.CriadoEm, a.DataElegibilidade, a.UsuarioId))
            .SingleAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the link of a deactivated station (L18) in the caller's transaction, after its atendimento left the
    /// in-progress states; an active station keeps its link. Keyed by station, not by session, because the RF12
    /// paths outside the guichê also end atendimentos on it. Returns the released link's session, null when
    /// nothing was released or the link belonged to a grant.
    /// </summary>
    public static async Task<Guid?> SoltarVinculoDeEstacaoInativaAsync(PafDbContext db, string tenantId, Guid estacaoId, CancellationToken ct)
    {
        ExigirTransacao(db);

        // Ativa is read by this statement, inside the transaction, never from a read taken before it.
        var soltos = await db.Database.SqlQuery<Guid?>($"""
            UPDATE paf."SessoesEstacao" v SET "LiberadoEm" = now()
              FROM paf."Estacoes" e
             WHERE v."TenantId" = {tenantId} AND v."EstacaoId" = {estacaoId} AND v."LiberadoEm" IS NULL
               AND e."TenantId" = v."TenantId" AND e."Id" = v."EstacaoId" AND NOT e."Ativa"
            RETURNING v."SessaoId" AS "Value"
            """).ToListAsync(ct).ConfigureAwait(false);
        return soltos.Count == 0 ? null : soltos[0];
    }

    public static void ExigirEstado(AtendimentoTravado atendimento, Guiche guiche, EstadoAtendimento esperado, string mensagem)
    {
        if (atendimento.Estado != esperado || atendimento.EstacaoId != guiche.Estacao.Id)
        {
            throw Conflito(mensagem);
        }
    }

    /// <summary>
    /// Writes the new state with its mark stamped by now() and increments Versao in the same UPDATE. The
    /// state guard in the WHERE makes a lost race visible as zero rows instead of a silent overwrite.
    /// </summary>
    public static async Task<int> TransitarAsync(PafDbContext db, AtendimentoTravado atendimento, FormattableString update, CancellationToken ct)
    {
        var versoes = await db.Database.SqlQuery<int>(update).ToListAsync(ct).ConfigureAwait(false);
        if (versoes.Count == 1)
        {
            return versoes[0];
        }

        throw Conflito(atendimento.Estado == EstadoAtendimento.EmAtendimento ? MensagensGuiche.MensagemNaoAtendendo : MensagensGuiche.MensagemNaoChamado);
    }

    /// <summary>
    /// Releases the other waiting atendimentos of the same ficha (seção 8 item 4): ElegivelDesde is rewritten
    /// and DataElegibilidade follows as a generated column; DataOperacional never moves. Runs under the lock.
    /// </summary>
    public static Task<List<Guid>> LiberarPendentesAsync(PafDbContext db, Guiche guiche, AtendimentoTravado atendimento, CancellationToken ct) =>
        LiberarPendentesAsync(db, guiche.TenantId, atendimento, ct);

    /// <summary>Same release for the paths outside the guichê; returns the filas of the released atendimentos.</summary>
    public static Task<List<Guid>> LiberarPendentesAsync(PafDbContext db, string tenantId, AtendimentoTravado atendimento, CancellationToken ct) =>
        db.Database.SqlQuery<Guid>($"""
            UPDATE paf."Atendimentos" SET "ElegivelDesde" = now()
             WHERE "TenantId" = {tenantId} AND "FichaId" = {atendimento.FichaId}
               AND "Id" <> {atendimento.Id} AND "Estado" = 'Aguardando'
            RETURNING "FilaId" AS "Value"
            """).ToListAsync(ct);

    /// <summary>Active painéis of the unit whose painel list includes the fila (RF04, bloco Painel Chamador).</summary>
    public static Task<List<Guid>> PaineisDaFilaAsync(PafDbContext db, Guid filaId, CancellationToken ct) =>
        (from ef in db.EstacaoFilas.AsNoTracking()
         join e in db.Estacoes.AsNoTracking() on ef.EstacaoId equals e.Id
         where ef.Papel == PapelEstacao.Painel && ef.FilaId == filaId && e.Ativa
         select e.Id).ToListAsync(ct);

    /// <summary>Seção 8 item 6: the notice is written to the outbox inside the transaction and sent directly only after the commit.</summary>
    public static async Task ConfirmarEAvisarAsync(IDbContextTransaction transacao, AvisosTempoReal avisos, AvisoPaf aviso, CancellationToken ct)
    {
        await avisos.GravarAsync(aviso, ct).ConfigureAwait(false);
        await transacao.CommitAsync(ct).ConfigureAwait(false);
        await avisos.EnviarAsync(aviso, ct).ConfigureAwait(false);
    }

    public static AtendimentoAtualizadoPaf AvisoDeEstado(Guiche guiche, AtendimentoTravado atendimento, int versao, EstadoAtendimento estado) =>
        AtendimentoAtualizadoPaf.Criar(guiche.TenantId, Correlacao(), new AtendimentoAtualizado(
            Guid.CreateVersion7(), atendimento.UnidadeId, atendimento.Id, atendimento.FichaId, versao, estado.ToString(), guiche.Estacao.Id));

    /// <summary>For the paths outside the guichê: the notice names the station the atendimento was on, if any.</summary>
    public static AtendimentoAtualizadoPaf AvisoDeEstado(string tenantId, AtendimentoTravado atendimento, int versao, EstadoAtendimento estado) =>
        AtendimentoAtualizadoPaf.Criar(tenantId, Correlacao(), new AtendimentoAtualizado(
            Guid.CreateVersion7(), atendimento.UnidadeId, atendimento.Id, atendimento.FichaId, versao, estado.ToString(), atendimento.EstacaoId));

    /// <summary>Letter followed by the raw number, no zero padding (seção 4.1).</summary>
    public static string Senha(string letra, int numero) =>
        string.Create(CultureInfo.InvariantCulture, $"{letra}{numero}");

    public static string Correlacao() => Activity.Current?.Id ?? Guid.NewGuid().ToString();

    public static CustomException Conflito(string mensagem) => new(mensagem, [], HttpStatusCode.Conflict);

    // ─── F04: cancelamento do RF12, fora do guichê ──────────────────

    public const string MensagemNaoCancelavel = "O atendimento já foi encerrado ou cancelado e não pode ser cancelado.";

    /// <summary>
    /// The RF12 cancel (requisitos seção 4 item 5, ARQ l.290-294): locks the ficha's rows and cancels the atendimento in
    /// any non-terminal state, with or without a guichê. Runs in the caller's transaction; the notices it returns are
    /// for <see cref="ConfirmarEAvisarTodosAsync"/>.
    /// </summary>
    public static async Task<AtendimentoCancelado> CancelarAsync(PafDbContext db, string tenantId, Guid atendimentoId, CancellationToken ct)
    {
        var atendimento = await TravarFichaAsync(db, tenantId, atendimentoId, ct).ConfigureAwait(false);
        return await CancelarTravadoAsync(db, tenantId, atendimento, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The cancel's write, for a caller that already holds the ficha lock (<see cref="TravarFichaAsync"/>) and judged
    /// the state it read under it. Only a Chamado or EmAtendimento releases the ficha's waiting atendimentos: an
    /// Aguardando never held the exclusivity, so its siblings stay as they are.
    /// </summary>
    public static async Task<AtendimentoCancelado> CancelarTravadoAsync(PafDbContext db, string tenantId, AtendimentoTravado atendimento, CancellationToken ct)
    {
        ExigirTransacao(db);
        if (atendimento.Estado is not (EstadoAtendimento.Aguardando or EstadoAtendimento.Chamado or EstadoAtendimento.EmAtendimento))
        {
            throw Conflito(MensagemNaoCancelavel);
        }

        // The state guard repeats the check above: under the lock it never misses, and if it did, zero rows is a 409, not an overwrite.
        var versoes = await db.Database.SqlQuery<int>($"""
            UPDATE paf."Atendimentos"
               SET "Estado" = 'Cancelado', "CanceladoEm" = now(), "Versao" = "Versao" + 1
             WHERE "TenantId" = {tenantId} AND "Id" = {atendimento.Id}
               AND "Estado" IN ('Aguardando', 'Chamado', 'EmAtendimento')
            RETURNING "Versao" AS "Value"
            """).ToListAsync(ct).ConfigureAwait(false);
        if (versoes.Count != 1)
        {
            throw Conflito(MensagemNaoCancelavel);
        }

        var filas = new HashSet<Guid> { atendimento.FilaId };
        if (atendimento.Estado is EstadoAtendimento.Chamado or EstadoAtendimento.EmAtendimento)
        {
            filas.UnionWith(await LiberarPendentesAsync(db, tenantId, atendimento, ct).ConfigureAwait(false));
        }

        if (atendimento.EstacaoId is { } estacaoId)
        {
            await SoltarVinculoDeEstacaoInativaAsync(db, tenantId, estacaoId, ct).ConfigureAwait(false);
        }

        // The owner guichê's screen and the painéis are all in the unit group, which both notices target.
        var avisos = new List<AvisoPaf> { AvisoDeEstado(tenantId, atendimento, versoes[0], EstadoAtendimento.Cancelado) };
        avisos.AddRange(filas.Select(fila => FilaAtualizadaPaf.Criar(tenantId, Correlacao(),
            new FilaAtualizada(Guid.CreateVersion7(), atendimento.UnidadeId, fila))));
        return new AtendimentoCancelado(atendimento.Id, versoes[0], avisos);
    }

    /// <summary>Seção 8 item 6 for a write with several notices: all written to the outbox before the commit, all sent after it.</summary>
    public static async Task ConfirmarEAvisarTodosAsync(IDbContextTransaction transacao, AvisosTempoReal avisos, IReadOnlyList<AvisoPaf> lista, CancellationToken ct)
    {
        foreach (var aviso in lista)
        {
            await avisos.GravarAsync(aviso, ct).ConfigureAwait(false);
        }

        await transacao.CommitAsync(ct).ConfigureAwait(false);
        foreach (var aviso in lista)
        {
            await avisos.EnviarAsync(aviso, ct).ConfigureAwait(false);
        }
    }

    // FOR UPDATE outside a transaction autocommits and holds nothing; fail loudly instead of locking nothing.
    private static void ExigirTransacao(PafDbContext db)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("O travamento da ficha exige transação aberta.");
        }
    }
}
