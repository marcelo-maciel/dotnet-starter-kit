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
}

/// <summary>The guichê of the session and the atendimento it acts on.</summary>
internal sealed record Guiche(string TenantId, Guid UsuarioId, Estacao Estacao);

/// <summary>Atendimento re-read after its ficha's rows were locked; the state check runs on this, never on a read from before the lock.</summary>
internal sealed record AtendimentoTravado(
    Guid Id, Guid UnidadeId, Guid FichaId, Guid FilaId, Guid? EstacaoId, EstadoAtendimento Estado, string Letra, int Numero);

/// <summary>
/// Shared steps of the guichê operations (seção 8): resolve the guichê from the session, lock every
/// atendimento row of the ficha in ascending id order, re-validate, then write with the PostgreSQL clock.
/// </summary>
internal static class OperacaoGuiche
{
    public static async Task<Guiche> ExigirAsync(PafDbContext db, SessaoCorrente sessao, CancellationToken ct)
    {
        var (tenantId, sessaoId, usuarioId) = sessao.Exigir();
        var vinculo = await VinculosGuiche.AtivoDaSessaoAsync(db, sessaoId, ct).ConfigureAwait(false)
            ?? throw Conflito(MensagensGuiche.MensagemSemGuiche);

        // Unit-filtered: a guichê of a unit the session left is not operable from here.
        var estacao = await db.Estacoes.AsNoTracking().FirstOrDefaultAsync(e => e.Id == vinculo.EstacaoId, ct).ConfigureAwait(false)
            ?? throw Conflito(MensagensGuiche.MensagemSemGuiche);
        return new Guiche(tenantId, usuarioId, estacao);
    }

    /// <summary>
    /// Locks all atendimentos of the ficha (waiting, ascending id: two operations on one ficha ask in the
    /// same order and never deadlock) and re-reads the target. Only the guichê that holds it may act on it.
    /// </summary>
    public static async Task<AtendimentoTravado> TravarAsync(PafDbContext db, Guiche guiche, Guid atendimentoId, CancellationToken ct)
    {
        var fichaId = await db.Atendimentos.AsNoTracking()
            .Where(a => a.Id == atendimentoId && a.EstacaoId == guiche.Estacao.Id)
            .Select(a => (Guid?)a.FichaId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException(MensagensGuiche.MensagemNaoEncontrado);

        // Raw SQL skips the global filters: tenant is explicit, and the ficha id came from a filtered read.
        await db.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM paf."Atendimentos"
             WHERE "TenantId" = {guiche.TenantId} AND "FichaId" = {fichaId}
             ORDER BY "Id"
               FOR UPDATE
            """).ToListAsync(ct).ConfigureAwait(false);

        return await (
                from a in db.Atendimentos.AsNoTracking()
                join f in db.Fichas.AsNoTracking() on a.FichaId equals f.Id
                where a.Id == atendimentoId
                select new AtendimentoTravado(a.Id, a.UnidadeId, a.FichaId, a.FilaId, a.EstacaoId, a.Estado, f.Letra, f.Numero))
            .SingleAsync(ct)
            .ConfigureAwait(false);
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
    public static Task<int> LiberarPendentesAsync(PafDbContext db, Guiche guiche, AtendimentoTravado atendimento, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            UPDATE paf."Atendimentos" SET "ElegivelDesde" = now()
             WHERE "TenantId" = {guiche.TenantId} AND "FichaId" = {atendimento.FichaId}
               AND "Id" <> {atendimento.Id} AND "Estado" = 'Aguardando'
            """, ct);

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

    /// <summary>Letter followed by the raw number, no zero padding (seção 4.1).</summary>
    public static string Senha(string letra, int numero) =>
        string.Create(CultureInfo.InvariantCulture, $"{letra}{numero}");

    public static string Correlacao() => Activity.Current?.Id ?? Guid.NewGuid().ToString();

    public static CustomException Conflito(string mensagem) => new(mensagem, [], HttpStatusCode.Conflict);
}
