using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Parametros;
using FSH.Modules.PAF.TempoReal;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ReabilitarAtendimentoDaFicha;

/// <summary>
/// RF12 "reabilitar" (requisitos RF12, seção 4; arquitetura seção 8 items 4 and 5): a cancelled atendimento goes
/// back to Aguardando on the same row. Under the ficha lock it re-checks the state, that the atendimento is still
/// callable by the "somente fichas da data atual" parameter (on the eligibility date BEFORE the rewrite, so
/// yesterday's atendimento is never pulled into today), and that no sibling is in progress. One UPDATE then ends
/// the previous eligibility cycle: ElegivelDesde = now() (DataElegibilidade follows, generated), the call, recall,
/// start and cancel marks and the station and user cleared; CriadoEm and DataOperacional never move, so the
/// atendimento keeps its place ahead of later arrivals, and the Rechamada rows stay as record.
/// </summary>
public sealed class ReabilitarAtendimentoDaFichaCommandHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    LeitorParametros parametros,
    AvisosTempoReal avisos,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : ICommandHandler<ReabilitarAtendimentoDaFichaCommand, AtendimentoAlteradoDto>
{
    public const string MensagemNaoCancelado = "Só um atendimento cancelado pode ser reabilitado.";
    public const string MensagemForaDoDia =
        "Este atendimento não pode mais ser chamado, porque é de outro dia. Emita uma ficha nova para o cliente.";

    public static string MensagemEmCurso(string nomeEstacao) =>
        $"A ficha está em atendimento no guichê {nomeEstacao}. Encerre ou cancele esse atendimento antes de reabilitar.";

    public async ValueTask<AtendimentoAlteradoDto> Handle(ReabilitarAtendimentoDaFichaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var unidadeId = unidadeCorrente.Exigir();
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();
        var somenteDataAtual = await parametros.LerAsync(CatalogoParametros.SomenteFichasDaDataAtual, unidadeId, cancellationToken);

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);
        var atendimento = await OperacaoGuiche.TravarFichaAsync(db, tenantId, command.AtendimentoId, cancellationToken);
        if (atendimento.Estado != EstadoAtendimento.Cancelado)
        {
            throw OperacaoGuiche.Conflito(MensagemNaoCancelado);
        }

        // now() is the transaction start, the same instant the UPDATE below stamps, so the check and the write agree on "today".
        if (somenteDataAtual && atendimento.DataElegibilidade != await PafRelogio.HojeAsync(db, cancellationToken))
        {
            throw OperacaoGuiche.Conflito(MensagemForaDoDia);
        }

        await RecusarSeOutroEmCursoAsync(atendimento, cancellationToken);

        var versoes = await db.Database.SqlQuery<int>($"""
            UPDATE paf."Atendimentos"
               SET "Estado" = 'Aguardando', "ElegivelDesde" = now(),
                   "ChamadoEm" = NULL, "UltimaRechamadaEm" = NULL, "IniciadoEm" = NULL, "CanceladoEm" = NULL,
                   "EstacaoId" = NULL, "UsuarioId" = NULL, "Versao" = "Versao" + 1
             WHERE "TenantId" = {tenantId} AND "Id" = {atendimento.Id} AND "Estado" = 'Cancelado'
            RETURNING "Versao" AS "Value"
            """).ToListAsync(cancellationToken);
        if (versoes.Count != 1)
        {
            throw OperacaoGuiche.Conflito(MensagemNaoCancelado);
        }

        // AtoImpersonacao (D28) is written here, in this transaction, once S11A's helper exists.
        var aviso = OperacaoGuiche.AvisoDeEstado(tenantId, atendimento, versoes[0], EstadoAtendimento.Aguardando);
        // The queue gained a waiting atendimento: guichês and lists re-read it, as after an emission.
        var fila = FilaAtualizadaPaf.Criar(tenantId, OperacaoGuiche.Correlacao(),
            new FilaAtualizada(Guid.CreateVersion7(), atendimento.UnidadeId, atendimento.FilaId));
        await avisos.GravarAsync(aviso, cancellationToken);
        await avisos.GravarAsync(fila, cancellationToken);
        await transacao.CommitAsync(cancellationToken);
        await avisos.EnviarAsync(aviso, cancellationToken);
        await avisos.EnviarAsync(fila, cancellationToken);

        return new AtendimentoAlteradoDto(atendimento.Id, nameof(EstadoAtendimento.Aguardando), versoes[0]);
    }

    // Read under the ficha lock: a call that committed while this request waited is seen here (seção 4, RF19 rule).
    private async Task RecusarSeOutroEmCursoAsync(AtendimentoTravado atendimento, CancellationToken ct)
    {
        var emCurso = await (
                from a in db.Atendimentos.AsNoTracking()
                join e in db.Estacoes.AsNoTracking() on a.EstacaoId equals e.Id
                where a.FichaId == atendimento.FichaId && a.Id != atendimento.Id
                      && (a.Estado == EstadoAtendimento.Chamado || a.Estado == EstadoAtendimento.EmAtendimento)
                select e.Nome)
            .FirstOrDefaultAsync(ct);
        if (emCurso is not null)
        {
            throw OperacaoGuiche.Conflito(MensagemEmCurso(emCurso));
        }
    }
}
