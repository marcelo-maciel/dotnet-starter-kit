using FSH.Modules.PAF.Contracts.v1.Guiches;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estacoes.DisableEstacao;

/// <summary>
/// What the deactivation did to the guichê: notices of the cancel, the session or impersonation grant whose link was
/// released, what stays in progress.
/// </summary>
internal sealed record DesfechoDoGuiche(
    IReadOnlyList<AvisoPaf> Avisos, Guid? SessaoSolta, Guid? ConcessaoSolta, bool Liberado, IReadOnlyList<AtendimentoEmCursoDto> EmCurso);

/// <summary>
/// Guichê side of the station deactivation (RF04, decided 03/10/2026): the link is released at once when nothing is in
/// progress on the station, or by the transaction that ends that atendimento (F01 helper), or now when the dialog asked
/// to cancel it by RF12.
/// </summary>
internal static class DesativacaoEstacao
{
    public static async Task<IReadOnlyList<AtendimentoEmCursoDto>> EmCursoAsync(PafDbContext db, Guid estacaoId, CancellationToken ct)
    {
        var linhas = await (
                from a in db.Atendimentos.AsNoTracking()
                join f in db.Fichas.AsNoTracking() on a.FichaId equals f.Id
                where a.EstacaoId == estacaoId
                    && (a.Estado == EstadoAtendimento.Chamado || a.Estado == EstadoAtendimento.EmAtendimento)
                orderby a.Id
                select new { a.Id, a.FichaId, f.Letra, f.Numero, a.Estado, a.Versao })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return linhas
            .Select(l => new AtendimentoEmCursoDto(l.Id, l.FichaId, OperacaoGuiche.Senha(l.Letra, l.Numero), l.Estado.ToString(), l.Versao))
            .ToList();
    }

    /// <summary>
    /// Runs in the deactivation's transaction, after <c>Ativa = false</c> was flushed. Lock order is the guichê's: the
    /// link row first, then the ficha rows, as the call and the switch take them.
    /// </summary>
    public static async Task<DesfechoDoGuiche> SoltarGuicheAsync(
        PafDbContext db, string tenantId, Guid estacaoId, Guid? cancelarAtendimentoId, CancellationToken ct)
    {
        var vinculo = await VinculosGuiche.TravarAtivoDaEstacaoAsync(db, tenantId, estacaoId, ct).ConfigureAwait(false);
        var avisos = new List<AvisoPaf>();
        if (cancelarAtendimentoId is { } alvo)
        {
            avisos.AddRange(await CancelarSeEmCursoAsync(db, tenantId, estacaoId, alvo, ct).ConfigureAwait(false));
        }

        // Re-read under the link lock: an attendant who ended it meanwhile did not release the link (the station still
        // looked active to his transaction), so the release falls to this one.
        var emCurso = await EmCursoAsync(db, estacaoId, ct).ConfigureAwait(false);
        if (vinculo is null)
        {
            return new DesfechoDoGuiche(avisos, null, null, false, emCurso);
        }

        if (emCurso.Count == 0)
        {
            await OperacaoGuiche.SoltarVinculoDeEstacaoInativaAsync(db, tenantId, estacaoId, ct).ConfigureAwait(false);
        }

        var liberado = await db.SessoesEstacao.AsNoTracking()
            .AnyAsync(v => v.Id == vinculo.Id && v.LiberadoEm != null, ct)
            .ConfigureAwait(false);
        return new DesfechoDoGuiche(avisos, liberado ? vinculo.SessaoId : null, liberado ? vinculo.ConcessaoId : null, liberado, emCurso);
    }

    // The dialog's choice may be stale: ended, cancelled or never on this station by now. Under the ficha lock the
    // state is re-read, and anything but an in-progress atendimento on this station is left alone, never a 409.
    private static async Task<IReadOnlyList<AvisoPaf>> CancelarSeEmCursoAsync(
        PafDbContext db, string tenantId, Guid estacaoId, Guid atendimentoId, CancellationToken ct)
    {
        var naEstacao = await db.Atendimentos.AsNoTracking()
            .AnyAsync(a => a.Id == atendimentoId && a.EstacaoId == estacaoId, ct)
            .ConfigureAwait(false);
        if (!naEstacao)
        {
            return [];
        }

        var atendimento = await OperacaoGuiche.TravarFichaAsync(db, tenantId, atendimentoId, ct).ConfigureAwait(false);
        if (atendimento.EstacaoId != estacaoId
            || atendimento.Estado is not (EstadoAtendimento.Chamado or EstadoAtendimento.EmAtendimento))
        {
            return [];
        }

        var cancelado = await OperacaoGuiche.CancelarTravadoAsync(db, tenantId, atendimento, ct).ConfigureAwait(false);
        return cancelado.Avisos;
    }
}
