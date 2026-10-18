using System.Net;
using System.Text.Json;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Parametros;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ChamarProximaFicha;

/// <summary>Row of the candidate query, materialized by EF from raw SQL.</summary>
internal sealed record Candidato(Guid AtendimentoId, Guid FichaId, Guid FilaId, Guid PrioridadeId, string Letra, int Numero);

/// <summary>
/// Calls the next ficha on the session's guichê (RF15, requisitos seção 4, arquitetura seção 8). Selection and
/// marking happen in one transaction; the call never waits: a candidate whose ficha has any row locked by
/// another operation is skipped. Losing UX_Atendimentos_FichaEmCurso retries in a new transaction; losing
/// UX_Atendimentos_EstacaoEmCurso (double click) is refused with no retry, because it would fail the same way.
/// </summary>
public sealed partial class ChamarProximaFichaCommandHandler(
    PafDbContext db,
    SessaoCorrente sessao,
    AvisosTempoReal avisos,
    LeitorParametros parametros,
    ILogger<ChamarProximaFichaCommandHandler> logger) : ICommandHandler<ChamarProximaFichaCommand, ChamadaDto>
{
    public const string RestricaoFichaEmCurso = "UX_Atendimentos_FichaEmCurso";
    public const string RestricaoEstacaoEmCurso = "UX_Atendimentos_EstacaoEmCurso";
    public const string MensagemSemFichas = "Não existem fichas na fila.";
    public const string MensagemEstacaoInativa = "Estação inativa não chama senhas.";
    public const string MensagemGuicheEmCurso = "O guichê já tem um atendimento em curso. Encerre ou cancele antes de chamar a próxima.";
    public const string MensagemDisputa = "Muitas chamadas simultâneas. Tente de novo.";

    /// <summary>Key of the RF04 guichê block option "respeitar ordem das filas" in Estacao.Configuracao.</summary>
    public const string ChaveRespeitarOrdemFilas = "respeitarOrdemFilas";

    // Each lost race means another guichê committed a call, so the queue moved; a handful of rounds is plenty.
    public const int MaxTentativas = 5;

    // Candidates skipped inside one transaction because another operation holds a row of their ficha.
    private const int MaxPulos = 20;

    public async ValueTask<ChamadaDto> Handle(ChamarProximaFichaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var guiche = await OperacaoGuiche.ExigirAsync(db, sessao, cancellationToken);

        // Refusals that only give the message (seção 8 item 1); the guarantee is UX_Atendimentos_EstacaoEmCurso.
        if (!guiche.Estacao.Ativa)
        {
            throw OperacaoGuiche.Conflito(MensagemEstacaoInativa);
        }

        var emCurso = await db.Atendimentos.AsNoTracking()
            .AnyAsync(a => a.EstacaoId == guiche.Estacao.Id
                && (a.Estado == EstadoAtendimento.Chamado || a.Estado == EstadoAtendimento.EmAtendimento), cancellationToken);
        if (emCurso)
        {
            throw OperacaoGuiche.Conflito(MensagemGuicheEmCurso);
        }

        var respeitaOrdem = RespeitaOrdemDasFilas(guiche.Estacao.Configuracao);
        var somenteDataAtual = await parametros.LerAsync(CatalogoParametros.SomenteFichasDaDataAtual, guiche.Estacao.UnidadeId, cancellationToken);
        for (var tentativa = 1; tentativa <= MaxTentativas; tentativa++)
        {
            try
            {
                var chamada = await TentarAsync(guiche, respeitaOrdem, somenteDataAtual, cancellationToken);
                if (tentativa > 1)
                {
                    LogChamadaRepetida(logger, guiche.TenantId, guiche.Estacao.Id, tentativa);
                }

                return chamada;
            }
            catch (Exception ex) when (PafRestricoes.Violou(ex, RestricaoEstacaoEmCurso))
            {
                throw OperacaoGuiche.Conflito(MensagemGuicheEmCurso);
            }
            catch (Exception ex) when (PafRestricoes.Violou(ex, RestricaoFichaEmCurso) || PafRestricoes.Impasse(ex))
            {
                // Another guichê put this ficha in progress first; the aborted transaction is gone, the next one
                // no longer sees the ficha as a candidate.
            }
        }

        LogChamadaEsgotada(logger, guiche.TenantId, guiche.Estacao.Id, MaxTentativas);
        throw new CustomException(MensagemDisputa, [], HttpStatusCode.ServiceUnavailable);
    }

    private async Task<ChamadaDto> TentarAsync(Guiche guiche, bool respeitaOrdem, bool somenteDataAtual, CancellationToken ct)
    {
        await using var transacao = await db.Database.BeginTransactionAsync(ct);

        // The guichê read before the transaction only feeds the early messages; under this lock a concurrent
        // switch or unit change either released it already (refused here) or waits for this call to commit.
        var estacaoDoVinculo = await VinculosGuiche.TravarAtivoDaSessaoAsync(db, guiche.TenantId, guiche.SessaoId, ct);
        if (estacaoDoVinculo != guiche.Estacao.Id)
        {
            throw OperacaoGuiche.Conflito(MensagensGuiche.MensagemSemGuiche);
        }

        var pulados = new List<Guid>();
        while (pulados.Count <= MaxPulos)
        {
            await transacao.CreateSavepointAsync("candidato", ct);
            var candidato = await CandidatoAsync(guiche, respeitaOrdem, somenteDataAtual, pulados, ct)
                ?? throw new NotFoundException(MensagemSemFichas);

            if (!await TravarFichaSemEsperarAsync(guiche, candidato.FichaId, ct))
            {
                // Rolling back to the savepoint releases the row locks taken for this candidate.
                await transacao.RollbackToSavepointAsync("candidato", ct);
                pulados.Add(candidato.FichaId);
                continue;
            }

            var versoes = await db.Database.SqlQuery<int>($"""
                UPDATE paf."Atendimentos"
                   SET "Estado" = 'Chamado', "EstacaoId" = {guiche.Estacao.Id}, "UsuarioId" = {guiche.UsuarioId},
                       "ChamadoEm" = now(), "Versao" = "Versao" + 1
                 WHERE "TenantId" = {guiche.TenantId} AND "Id" = {candidato.AtendimentoId} AND "Estado" = 'Aguardando'
                RETURNING "Versao" AS "Value"
                """).ToListAsync(ct);
            if (versoes.Count == 0)
            {
                await transacao.RollbackToSavepointAsync("candidato", ct);
                pulados.Add(candidato.FichaId);
                continue;
            }

            await db.Database.ExecuteSqlAsync($"""
                UPDATE paf."Filas" SET "UltimaChamadaEm" = now()
                 WHERE "TenantId" = {guiche.TenantId} AND "Id" = {candidato.FilaId}
                """, ct);

            return await ConfirmarAsync(transacao, guiche, candidato, versoes[0], ct);
        }

        throw new CustomException(MensagemDisputa, [], HttpStatusCode.ServiceUnavailable);
    }

    private async Task<ChamadaDto> ConfirmarAsync(IDbContextTransaction transacao, Guiche guiche, Candidato candidato, int versao, CancellationToken ct)
    {
        var senha = OperacaoGuiche.Senha(candidato.Letra, candidato.Numero);
        var paineis = await OperacaoGuiche.PaineisDaFilaAsync(db, candidato.FilaId, ct);
        var chamada = SenhaChamadaPaf.Criar(guiche.TenantId, OperacaoGuiche.Correlacao(), new SenhaChamada(
            Guid.CreateVersion7(), guiche.Estacao.UnidadeId, candidato.AtendimentoId, candidato.FichaId, versao,
            senha, guiche.Estacao.Id, guiche.Estacao.Nome), paineis);
        var fila = FilaAtualizadaPaf.Criar(guiche.TenantId, OperacaoGuiche.Correlacao(),
            new FilaAtualizada(Guid.CreateVersion7(), guiche.Estacao.UnidadeId, candidato.FilaId));

        await avisos.GravarAsync(chamada, ct);
        await avisos.GravarAsync(fila, ct);
        await transacao.CommitAsync(ct);
        await avisos.EnviarAsync(chamada, ct);
        await avisos.EnviarAsync(fila, ct);

        return new ChamadaDto(candidato.AtendimentoId, candidato.FichaId, senha, candidato.FilaId, candidato.PrioridadeId, versao);
    }

    private async Task<Candidato?> CandidatoAsync(Guiche guiche, bool respeitaOrdem, bool somenteDataAtual, List<Guid> pulados, CancellationToken ct)
    {
        var candidatos = await db.Database.SqlQuery<Candidato>(ConsultaCandidato(
            guiche.TenantId, guiche.Estacao.UnidadeId, guiche.Estacao.Id, respeitaOrdem, somenteDataAtual, pulados.ToArray())).ToListAsync(ct);
        return candidatos.Count == 0 ? null : candidatos[0];
    }

    /// <summary>
    /// Requisitos seção 4: fila of the guichê role (active or not), prioridade enabled on the station, waiting,
    /// eligible (today only when RF06 "somente fichas da data atual" is on), ficha older than five seconds, no
    /// other atendimento of the ficha in progress. Order: station's fila order when it respects it, then
    /// prioridade value descending, then creation order. Public so the plan test runs EXPLAIN on this exact text.
    /// The date test is a lower bound (">= today", same as "= today" because ElegivelDesde only takes now()) so
    /// DataElegibilidade stays a range condition on IX_Atendimentos_Candidatos with the parameter on or off.
    /// </summary>
    public static FormattableString ConsultaCandidato(
        string tenantId, Guid unidadeId, Guid estacaoId, bool respeitaOrdem, bool somenteDataAtual, Guid[] fichasPuladas) => $"""
            SELECT a."Id" AS "AtendimentoId", a."FichaId", a."FilaId", a."PrioridadeId", f."Letra", f."Numero"
              FROM paf."Atendimentos" a
              JOIN paf."Fichas" f ON f."Id" = a."FichaId"
              JOIN paf."EstacaoFilas" ef
                ON ef."EstacaoId" = {estacaoId} AND ef."Papel" = 'Guiche' AND ef."FilaId" = a."FilaId"
              JOIN paf."EstacaoPrioridades" ep
                ON ep."EstacaoId" = {estacaoId} AND ep."PrioridadeId" = a."PrioridadeId"
              JOIN paf."Prioridades" p ON p."Id" = a."PrioridadeId"
             WHERE a."TenantId" = {tenantId} AND a."UnidadeId" = {unidadeId}
               AND a."Estado" = 'Aguardando'
               AND a."DataElegibilidade" >= CASE WHEN {somenteDataAtual}
                   THEN (now() AT TIME ZONE 'America/Sao_Paulo')::date ELSE '-infinity'::date END
               AND f."CriadoEm" <= now() - interval '5 seconds'
               AND a."FichaId" <> ALL({fichasPuladas})
               AND NOT EXISTS (
                   SELECT 1 FROM paf."Atendimentos" o
                    WHERE o."TenantId" = {tenantId} AND o."FichaId" = a."FichaId" AND o."Estado" IN ('Chamado', 'EmAtendimento'))
             ORDER BY CASE WHEN {respeitaOrdem} THEN ef."Ordem" ELSE 0 END, p."Valor" DESC, a."CriadoEm", a."Id"
             LIMIT 1
               FOR UPDATE OF a SKIP LOCKED
            """;

    /// <summary>
    /// Locks every atendimento row of the ficha in ascending id order without waiting. Fewer rows locked than
    /// exist means another operation (cancel, recall, rehabilitate) holds one: the call gives up this ficha.
    /// </summary>
    private async Task<bool> TravarFichaSemEsperarAsync(Guiche guiche, Guid fichaId, CancellationToken ct)
    {
        var travadas = await db.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM paf."Atendimentos"
             WHERE "TenantId" = {guiche.TenantId} AND "FichaId" = {fichaId}
             ORDER BY "Id"
               FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        var existentes = await db.Atendimentos.AsNoTracking().CountAsync(a => a.FichaId == fichaId, ct);
        if (travadas.Count != existentes)
        {
            return false;
        }

        // Re-validated under the lock: an atendimento of the ficha may have entered progress meanwhile.
        return !await db.Atendimentos.AsNoTracking()
            .AnyAsync(a => a.FichaId == fichaId && (a.Estado == EstadoAtendimento.Chamado || a.Estado == EstadoAtendimento.EmAtendimento), ct);
    }

    /// <summary>
    /// ponytail: the RF04 guichê block has no cadastro screen yet; the option lives in the station's JSON
    /// configuration and is off when absent. Move it to a column when the block gets its endpoint.
    /// </summary>
    internal static bool RespeitaOrdemDasFilas(string? configuracao)
    {
        if (string.IsNullOrWhiteSpace(configuracao))
        {
            return false;
        }

        using var json = JsonDocument.Parse(configuracao);
        return json.RootElement.ValueKind == JsonValueKind.Object
            && json.RootElement.TryGetProperty(ChaveRespeitarOrdemFilas, out var valor)
            && valor.ValueKind == JsonValueKind.True;
    }

    [LoggerMessage(EventId = 9040, Level = LogLevel.Information,
        Message = "Chamada repetida em transacao nova apos disputa de ficha; tenant={TenantId} guiche={EstacaoId} tentativas={Tentativas}")]
    private static partial void LogChamadaRepetida(ILogger logger, string tenantId, Guid estacaoId, int tentativas);

    [LoggerMessage(EventId = 9041, Level = LogLevel.Error,
        Message = "Chamada esgotou as tentativas; tenant={TenantId} guiche={EstacaoId} tentativas={Tentativas}")]
    private static partial void LogChamadaEsgotada(ILogger logger, string tenantId, Guid estacaoId, int tentativas);
}
