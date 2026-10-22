using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Atendimentos.ChamarFichaEscolhida;
using FSH.Modules.PAF.Features.v1.Atendimentos.ChamarProximaFicha;
using FSH.Modules.PAF.Features.v1.Atendimentos.ReabilitarAtendimentoDaFicha;
using FSH.Modules.PAF.Parametros;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ChamarFichaVip;

/// <summary>
/// RF19 "atendimento VIP": the guichê calls the ficha the attendant picked from the VIP search (printed senha and
/// date, D02, ListFichasVipQueryHandler) in the given fila, out of the queue order and nothing else (requisitos RF19,
/// 8.1 l.1030). Whether its atendimento is still callable follows the atendimento's eligibility date, through the same
/// predicate as every other call (ChamadaDeFicha). A ficha in progress on another guichê is refused naming that guichê. Same lock order as
/// chamar-próximo and the chosen call (guichê link, then ficha rows), waiting for the ficha lock like the chosen call.
/// </summary>
public sealed class ChamarFichaVipCommandHandler(
    PafDbContext db,
    SessaoCorrente sessao, ConcessaoCorrente concessao, AtosImpersonacao atos,
    AvisosTempoReal avisos,
    LeitorParametros parametros) : ICommandHandler<ChamarFichaVipCommand, ChamadaDto>
{
    public const string MensagemNaoEncontrada = "Esta ficha não está aguardando nesta fila nesta unidade.";

    public static string MensagemEmCurso(string nomeEstacao) =>
        $"A ficha está em atendimento no guichê {nomeEstacao}.";

    public async ValueTask<ChamadaDto> Handle(ChamarFichaVipCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var guiche = await OperacaoGuiche.ExigirAsync(db, DonoPosto.Exigir(sessao, concessao), cancellationToken);
        if (!guiche.Estacao.Ativa)
        {
            throw OperacaoGuiche.Conflito(ChamarProximaFichaCommandHandler.MensagemEstacaoInativa);
        }

        // Only gives the message before trying; the guarantee is UX_Atendimentos_EstacaoEmCurso (ARQ l.144).
        var guicheEmCurso = await db.Atendimentos.AsNoTracking()
            .AnyAsync(a => a.EstacaoId == guiche.Estacao.Id
                && (a.Estado == EstadoAtendimento.Chamado || a.Estado == EstadoAtendimento.EmAtendimento), cancellationToken);
        if (guicheEmCurso)
        {
            throw OperacaoGuiche.Conflito(ChamarProximaFichaCommandHandler.MensagemGuicheEmCurso);
        }

        var atendimentoId = await LocalizarAsync(command.FichaId, command.FilaId, cancellationToken);
        var somenteDataAtual = await parametros.LerAsync(CatalogoParametros.SomenteFichasDaDataAtual, guiche.Estacao.UnidadeId, cancellationToken);
        try
        {
            return await ChamarAsync(guiche, atendimentoId, somenteDataAtual, cancellationToken);
        }
        catch (Exception ex) when (PafRestricoes.Violou(ex, ChamarProximaFichaCommandHandler.RestricaoEstacaoEmCurso))
        {
            throw OperacaoGuiche.Conflito(ChamarProximaFichaCommandHandler.MensagemGuicheEmCurso);
        }
        catch (Exception ex) when (PafRestricoes.Violou(ex, ChamarProximaFichaCommandHandler.RestricaoFichaEmCurso))
        {
            throw OperacaoGuiche.Conflito(ChamarFichaEscolhidaCommandHandler.MensagemJaChamada);
        }
        catch (Exception ex) when (PafRestricoes.Impasse(ex))
        {
            throw new CustomException(ChamarProximaFichaCommandHandler.MensagemDisputa, [], HttpStatusCode.ServiceUnavailable);
        }
    }

    /// <summary>
    /// Unit-filtered reads, so another unit's or tenant's ficha id is simply not found. Exactly one waiting atendimento
    /// of the ficha in the fila, else 404.
    /// </summary>
    private async Task<Guid> LocalizarAsync(Guid fichaId, Guid filaId, CancellationToken ct)
    {
        var aguardando = await db.Atendimentos.AsNoTracking()
            .Where(a => a.FichaId == fichaId && a.FilaId == filaId && a.Estado == EstadoAtendimento.Aguardando)
            .Select(a => a.Id)
            .Take(2)
            .ToListAsync(ct);
        if (aguardando.Count == 1)
        {
            return aguardando[0];
        }

        // Nothing waiting in that fila: when the ficha is being served somewhere, say where instead of "not found" (RF19).
        await RecusarSeEmCursoAsync(fichaId, ct);
        throw new NotFoundException(MensagemNaoEncontrada);
    }

    private async Task<ChamadaDto> ChamarAsync(Guiche guiche, Guid atendimentoId, bool somenteDataAtual, CancellationToken ct)
    {
        await using var transacao = await db.Database.BeginTransactionAsync(ct);

        var estacaoDoVinculo = await VinculosGuiche.TravarAtivoDoDonoAsync(db, guiche.Dono, ct);
        if (estacaoDoVinculo != guiche.Estacao.Id)
        {
            throw OperacaoGuiche.Conflito(MensagensGuiche.MensagemSemGuiche);
        }

        var atendimento = await OperacaoGuiche.TravarFichaAsync(db, guiche.TenantId, atendimentoId, ct);

        // Under the lock: a call that committed while this request waited is seen here and named.
        await RecusarSeEmCursoAsync(atendimento.FichaId, ct);
        if (atendimento.Estado != EstadoAtendimento.Aguardando)
        {
            throw new NotFoundException(MensagemNaoEncontrada);
        }

        var elegiveis = await db.Database.SqlQuery<Candidato>(ChamarFichaEscolhidaCommandHandler.ConsultaEscolhida(
            guiche.TenantId, guiche.Estacao.UnidadeId, guiche.Estacao.Id, somenteDataAtual, atendimentoId)).ToListAsync(ct);
        if (elegiveis.Count == 0)
        {
            // RF19: an atendimento the date parameter made uncallable gets the same refusal as the RF12 reabilitar.
            var foraDoDia = somenteDataAtual && atendimento.DataElegibilidade != await PafRelogio.HojeAsync(db, ct);
            throw OperacaoGuiche.Conflito(foraDoDia
                ? ReabilitarAtendimentoDaFichaCommandHandler.MensagemForaDoDia
                : ChamarFichaEscolhidaCommandHandler.MensagemNaoElegivel);
        }

        var candidato = elegiveis[0];
        var versao = await ChamadaDeFicha.MarcarChamadoAsync(db, guiche, candidato, ct)
            ?? throw OperacaoGuiche.Conflito(ChamarFichaEscolhidaCommandHandler.MensagemJaChamada);
        return await ChamadaDeFicha.ConfirmarAsync(db, avisos, atos, transacao, guiche, candidato, versao, ct);
    }

    private async Task RecusarSeEmCursoAsync(Guid fichaId, CancellationToken ct)
    {
        var emCurso = await (
                from a in db.Atendimentos.AsNoTracking()
                join e in db.Estacoes.AsNoTracking() on a.EstacaoId equals e.Id
                where a.FichaId == fichaId
                      && (a.Estado == EstadoAtendimento.Chamado || a.Estado == EstadoAtendimento.EmAtendimento)
                select e.Nome)
            .FirstOrDefaultAsync(ct);
        if (emCurso is not null)
        {
            throw OperacaoGuiche.Conflito(MensagemEmCurso(emCurso));
        }
    }
}
