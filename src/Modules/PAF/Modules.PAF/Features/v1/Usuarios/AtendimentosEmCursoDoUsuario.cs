using FSH.Modules.PAF.Contracts.v1.Usuarios;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Usuarios;

/// <summary>
/// The Chamado and EmAtendimento atendimentos a user holds, in every unidade, and their RF12 cancel (RF08 scope edit,
/// RF22 deactivation): the screen shows them and offers to release them in the same act. An explicit cross-unit path
/// (<c>IgnoreQueryFilters([PafQueryFilters.Unidade])</c>): the unidade taken out, or any unidade of a deactivated user,
/// is rarely the caller's current one. Callers check first that the user fits in their scope (RF08), which bounds
/// these unidades to the caller's own.
/// </summary>
public sealed class AtendimentosEmCursoDoUsuario(PafDbContext db, IUnidadeCorrente unidadeCorrente, AtosImpersonacao atos)
{
    public async Task<IReadOnlyList<AtendimentoEmCursoDoUsuarioDto>> ListarAsync(Guid usuarioId, CancellationToken ct)
    {
        var linhas = await (
                from a in db.Atendimentos.IgnoreQueryFilters([PafQueryFilters.Unidade]).AsNoTracking()
                join f in db.Fichas.IgnoreQueryFilters([PafQueryFilters.Unidade]).AsNoTracking() on a.FichaId equals f.Id
                where a.UsuarioId == usuarioId
                      && (a.Estado == EstadoAtendimento.Chamado || a.Estado == EstadoAtendimento.EmAtendimento)
                orderby a.UnidadeId, a.Id
                select new { a.Id, a.UnidadeId, f.Letra, f.Numero, a.Estado, a.EstacaoId })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return [.. linhas.Select(l => new AtendimentoEmCursoDoUsuarioDto(
            l.Id, l.UnidadeId, OperacaoGuiche.Senha(l.Letra, l.Numero), l.Estado.ToString(), l.EstacaoId))];
    }

    /// <summary>
    /// Cancels, in the caller's open transaction, the user's atendimentos in progress in <paramref name="unidades"/> and
    /// returns the notices for <see cref="OperacaoGuiche.ConfirmarEAvisarTodosAsync"/>. Each one goes through the RF12
    /// service under its ficha lock; one that left the user or the in-progress states between the read and the lock is
    /// skipped, never cancelled on someone else's behalf.
    /// </summary>
    public async Task<IReadOnlyList<AvisoPaf>> CancelarAsync(string tenantId, Guid usuarioId, IReadOnlySet<Guid> unidades, CancellationToken ct)
    {
        var alvos = (await ListarAsync(usuarioId, ct).ConfigureAwait(false)).Where(a => unidades.Contains(a.UnidadeId)).ToList();
        var avisos = new List<AvisoPaf>();
        if (alvos.Count == 0)
        {
            return avisos;
        }

        // The ficha lock and the RF12 cancel read through the unit filter, so each unidade becomes the current one while
        // its atendimentos are cancelled, and the caller's unidade comes back afterwards.
        var original = unidadeCorrente.UnidadeId;
        foreach (var unidade in alvos.GroupBy(a => a.UnidadeId))
        {
            unidadeCorrente.Definir(unidade.Key);
            foreach (var alvo in unidade)
            {
                var travado = await OperacaoGuiche.TravarFichaAsync(db, tenantId, alvo.AtendimentoId, ct).ConfigureAwait(false);
                bool aindaDoUsuario = await db.Atendimentos.AsNoTracking()
                    .AnyAsync(a => a.Id == alvo.AtendimentoId && a.UsuarioId == usuarioId
                        && (a.Estado == EstadoAtendimento.Chamado || a.Estado == EstadoAtendimento.EmAtendimento), ct)
                    .ConfigureAwait(false);
                if (!aindaDoUsuario)
                {
                    continue;
                }

                var cancelado = await OperacaoGuiche.CancelarTravadoAsync(db, tenantId, travado, ct).ConfigureAwait(false);
                // The cancel is raw SQL, so the impersonation act (D28) is written explicitly in this transaction.
                await atos.GravarAsync(alvo.AtendimentoId, ct).ConfigureAwait(false);
                avisos.AddRange(cancelado.Avisos);
            }
        }

        if (original is { } unidadeDoChamador)
        {
            unidadeCorrente.Definir(unidadeDoChamador);
        }

        return avisos;
    }
}
