using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Atendimentos.ChamarProximaFicha;
using FSH.Modules.PAF.Parametros;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ChamarFichaEscolhida;

/// <summary>
/// Calls the atendimento the attendant chose (RF04 "selecionar ficha para atendimento", requisitos seção 4, ARQ seção 8
/// item 5). Unlike chamar-próximo it waits for the ficha lock instead of skipping: the attendant named this ficha, so
/// there is no next candidate to move on to. Under the lock the choice is re-validated by the same predicate the call
/// uses; a ficha another guichê called first is refused with no write and no retry, so the screen reloads its list.
/// </summary>
public sealed class ChamarFichaEscolhidaCommandHandler(
    PafDbContext db,
    SessaoCorrente sessao, ConcessaoCorrente concessao, AtosImpersonacao atos,
    AvisosTempoReal avisos,
    LeitorParametros parametros) : ICommandHandler<ChamarFichaEscolhidaCommand, ChamadaDto>
{
    public const string MensagemJaChamada = "A ficha já foi chamada por outro guichê.";
    public const string MensagemNaoElegivel = "A ficha não pode ser chamada por este guichê.";

    public async ValueTask<ChamadaDto> Handle(ChamarFichaEscolhidaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var guiche = await OperacaoGuiche.ExigirAsync(db, DonoPosto.Exigir(sessao, concessao), cancellationToken);
        if (!guiche.Estacao.Ativa)
        {
            throw OperacaoGuiche.Conflito(ChamarProximaFichaCommandHandler.MensagemEstacaoInativa);
        }

        if (!ChamadaDeFicha.OpcaoLigada(guiche.Estacao.Configuracao, ChamadaDeFicha.ChaveSelecionarFicha))
        {
            throw OperacaoGuiche.Conflito(ChamadaDeFicha.MensagemSemSelecao);
        }

        // Only gives the message before trying; the guarantee is UX_Atendimentos_EstacaoEmCurso (ARQ l.144).
        var emCurso = await db.Atendimentos.AsNoTracking()
            .AnyAsync(a => a.EstacaoId == guiche.Estacao.Id
                && (a.Estado == EstadoAtendimento.Chamado || a.Estado == EstadoAtendimento.EmAtendimento), cancellationToken);
        if (emCurso)
        {
            throw OperacaoGuiche.Conflito(ChamarProximaFichaCommandHandler.MensagemGuicheEmCurso);
        }

        var somenteDataAtual = await parametros.LerAsync(CatalogoParametros.SomenteFichasDaDataAtual, guiche.Estacao.UnidadeId, cancellationToken);
        try
        {
            return await ChamarAsync(guiche, command.AtendimentoId, somenteDataAtual, cancellationToken);
        }
        catch (Exception ex) when (PafRestricoes.Violou(ex, ChamarProximaFichaCommandHandler.RestricaoEstacaoEmCurso))
        {
            throw OperacaoGuiche.Conflito(ChamarProximaFichaCommandHandler.MensagemGuicheEmCurso);
        }
        catch (Exception ex) when (PafRestricoes.Violou(ex, ChamarProximaFichaCommandHandler.RestricaoFichaEmCurso))
        {
            throw OperacaoGuiche.Conflito(MensagemJaChamada);
        }
        catch (Exception ex) when (PafRestricoes.Impasse(ex))
        {
            throw new CustomException(ChamarProximaFichaCommandHandler.MensagemDisputa, [], HttpStatusCode.ServiceUnavailable);
        }
    }

    private async Task<ChamadaDto> ChamarAsync(Guiche guiche, Guid atendimentoId, bool somenteDataAtual, CancellationToken ct)
    {
        await using var transacao = await db.Database.BeginTransactionAsync(ct);

        // Same lock order as chamar-próximo (guichê link, then ficha rows), so the two never deadlock each other.
        var estacaoDoVinculo = await VinculosGuiche.TravarAtivoDoDonoAsync(db, guiche.Dono, ct);
        if (estacaoDoVinculo != guiche.Estacao.Id)
        {
            throw OperacaoGuiche.Conflito(MensagensGuiche.MensagemSemGuiche);
        }

        // Unit-filtered entry: an atendimento of another unit is 404 before any lock.
        var atendimento = await OperacaoGuiche.TravarFichaAsync(db, guiche.TenantId, atendimentoId, ct);
        if (atendimento.Estado != EstadoAtendimento.Aguardando || await FichaEmCursoAsync(atendimento.FichaId, ct))
        {
            throw OperacaoGuiche.Conflito(MensagemJaChamada);
        }

        // The ficha rows are locked, so this read cannot go stale before the UPDATE below.
        var elegiveis = await db.Database.SqlQuery<Candidato>(ConsultaEscolhida(
            guiche.TenantId, guiche.Estacao.UnidadeId, guiche.Estacao.Id, somenteDataAtual, atendimentoId)).ToListAsync(ct);
        if (elegiveis.Count == 0)
        {
            throw OperacaoGuiche.Conflito(MensagemNaoElegivel);
        }

        var candidato = elegiveis[0];
        var versao = await ChamadaDeFicha.MarcarChamadoAsync(db, guiche, candidato, ct)
            ?? throw OperacaoGuiche.Conflito(MensagemJaChamada);
        return await ChamadaDeFicha.ConfirmarAsync(db, avisos, atos, transacao, guiche, candidato, versao, ct);
    }

    private Task<bool> FichaEmCursoAsync(Guid fichaId, CancellationToken ct) =>
        db.Atendimentos.AsNoTracking()
            .AnyAsync(a => a.FichaId == fichaId && (a.Estado == EstadoAtendimento.Chamado || a.Estado == EstadoAtendimento.EmAtendimento), ct);

    /// <summary>The call's eligibility predicate narrowed to the chosen atendimento; no order, no lock (the caller holds the ficha).</summary>
    internal static FormattableString ConsultaEscolhida(string tenantId, Guid unidadeId, Guid estacaoId, bool somenteDataAtual, Guid atendimentoId) =>
        ChamadaDeFicha.Concatenar(
            $"""
            SELECT a."Id" AS "AtendimentoId", a."FichaId", a."FilaId", a."PrioridadeId", f."Letra", f."Numero"
            """,
            ChamadaDeFicha.Elegiveis(tenantId, unidadeId, estacaoId, somenteDataAtual),
            $"""
               AND a."Id" = {atendimentoId}
            """);
}
