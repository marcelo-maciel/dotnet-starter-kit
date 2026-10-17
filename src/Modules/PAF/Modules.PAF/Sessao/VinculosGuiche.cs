using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Sessao;

/// <summary>Regras do vínculo entre sessão e guichê (<see cref="SessaoEstacao"/>) usadas pela troca de unidade e de guichê.</summary>
internal static class VinculosGuiche
{
    public const string MensagemAtendimentoEmCurso =
        "Há um atendimento em curso no seu guichê. Encerre ou encaminhe antes de trocar.";

    public static Task<SessaoEstacao?> AtivoDaSessaoAsync(PafDbContext db, Guid sessaoId, CancellationToken ct) =>
        db.SessoesEstacao.AsNoTracking().FirstOrDefaultAsync(v => v.SessaoId == sessaoId && v.LiberadoEm == null, ct);

    public static Task<SessaoEstacao?> AtivoDaEstacaoAsync(PafDbContext db, Guid estacaoId, CancellationToken ct) =>
        db.SessoesEstacao.AsNoTracking().FirstOrDefaultAsync(v => v.EstacaoId == estacaoId && v.LiberadoEm == null, ct);

    /// <summary>Solta o vínculo do guichê, recusando quando há atendimento chamado ou em atendimento nele.</summary>
    public static async Task SoltarSemAtendimentoEmCursoAsync(PafDbContext db, SessaoEstacao vinculo, CancellationToken ct)
    {
        // The station belongs to the unit being left, which may differ from the current one: only the unit filter is lifted.
        var emCurso = await db.Atendimentos.AsNoTracking()
            .IgnoreQueryFilters([PafQueryFilters.Unidade])
            .AnyAsync(a => a.EstacaoId == vinculo.EstacaoId
                && (a.Estado == EstadoAtendimento.Chamado || a.Estado == EstadoAtendimento.EmAtendimento), ct);
        if (emCurso)
        {
            throw new CustomException(MensagemAtendimentoEmCurso, [], System.Net.HttpStatusCode.Conflict);
        }

        await SoltarAsync(db, vinculo.Id, ct);
    }

    public static Task<int> SoltarAsync(PafDbContext db, Guid vinculoId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"""UPDATE paf."SessoesEstacao" SET "LiberadoEm" = now() WHERE "Id" = {vinculoId} AND "LiberadoEm" IS NULL""",
            ct);
}
