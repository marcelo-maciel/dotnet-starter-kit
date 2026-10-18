using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Features.v1.Atendimentos.ChamarProximaFicha;
using FSH.Modules.PAF.Parametros;
using FSH.Modules.PAF.Sessao;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ListFichasEmEspera;

/// <summary>Row of the waiting list, materialized by EF from raw SQL.</summary>
internal sealed record FichaEmEspera(Guid AtendimentoId, Guid FichaId, Guid FilaId, Guid PrioridadeId, string Letra, int Numero, DateTimeOffset ElegivelDesde);

/// <summary>
/// What the guichê could call, in the order chamar-próximo would call it (requisitos seção 4: "a lista mostra os
/// atendimentos aguardando na ordem em que a chamada os escolheria"). Same predicate and order as the call, so the
/// first row is the next one; a plain read, no lock, so a row may be gone by the time it is chosen (the call re-validates).
/// </summary>
public sealed class ListFichasEmEsperaQueryHandler(PafDbContext db, SessaoCorrente sessao, LeitorParametros parametros)
    : IQueryHandler<ListFichasEmEsperaQuery, IReadOnlyList<FichaEmEsperaDto>>
{
    // ponytail: a fixed cap instead of paging; a guichê queue is tens of fichas. Page by (order key) when a client needs more.
    public const int MaxFichas = 200;

    public async ValueTask<IReadOnlyList<FichaEmEsperaDto>> Handle(ListFichasEmEsperaQuery query, CancellationToken cancellationToken)
    {
        var guiche = await OperacaoGuiche.ExigirAsync(db, sessao, cancellationToken);
        if (!ChamadaDeFicha.OpcaoLigada(guiche.Estacao.Configuracao, ChamadaDeFicha.ChaveSelecionarFicha))
        {
            throw OperacaoGuiche.Conflito(ChamadaDeFicha.MensagemSemSelecao);
        }

        var respeitaOrdem = ChamarProximaFichaCommandHandler.RespeitaOrdemDasFilas(guiche.Estacao.Configuracao);
        var somenteDataAtual = await parametros.LerAsync(CatalogoParametros.SomenteFichasDaDataAtual, guiche.Estacao.UnidadeId, cancellationToken);
        var linhas = await db.Database.SqlQuery<FichaEmEspera>(ConsultaEmEspera(
            guiche.TenantId, guiche.Estacao.UnidadeId, guiche.Estacao.Id, respeitaOrdem, somenteDataAtual)).ToListAsync(cancellationToken);
        return linhas
            .Select(l => new FichaEmEsperaDto(l.AtendimentoId, l.FichaId, OperacaoGuiche.Senha(l.Letra, l.Numero), l.FilaId, l.PrioridadeId, l.ElegivelDesde))
            .ToList();
    }

    internal static FormattableString ConsultaEmEspera(string tenantId, Guid unidadeId, Guid estacaoId, bool respeitaOrdem, bool somenteDataAtual) =>
        ChamadaDeFicha.Concatenar(
            $"""
            SELECT a."Id" AS "AtendimentoId", a."FichaId", a."FilaId", a."PrioridadeId", f."Letra", f."Numero", a."ElegivelDesde"
            """,
            ChamadaDeFicha.Elegiveis(tenantId, unidadeId, estacaoId, somenteDataAtual),
            ChamadaDeFicha.OrdemDaChamada(respeitaOrdem),
            $"""
             LIMIT {MaxFichas}
            """);
}
