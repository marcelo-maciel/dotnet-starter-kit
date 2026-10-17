using FSH.Modules.PAF.Contracts.v1.Guiches;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.Sessao;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Guiches.ObterPosto;

/// <summary>
/// The server's view of the session's post, so the guichê screen never trusts its own memory after a reload.
/// The guichê is read with the unit filter: a link to another unit's station is not this unit's post.
/// </summary>
public sealed class ObterPostoGuicheQueryHandler(PafDbContext db, SessaoCorrente sessao, IUnidadeCorrente unidadeCorrente)
    : IQueryHandler<ObterPostoGuicheQuery, PostoGuicheDto>
{
    public async ValueTask<PostoGuicheDto> Handle(ObterPostoGuicheQuery query, CancellationToken cancellationToken)
    {
        var (_, sessaoId, _) = sessao.Exigir();
        if (unidadeCorrente.UnidadeId is not { } unidadeId)
        {
            return new PostoGuicheDto(null, null, null, null, null);
        }

        var unidade = await db.Unidades.AsNoTracking()
            .Where(u => u.Id == unidadeId)
            .Select(u => u.Nome)
            .FirstOrDefaultAsync(cancellationToken);
        var guiche = await (
                from v in db.SessoesEstacao.AsNoTracking()
                join e in db.Estacoes.AsNoTracking() on v.EstacaoId equals e.Id
                where v.SessaoId == sessaoId && v.LiberadoEm == null
                select new { e.Id, e.Nome })
            .FirstOrDefaultAsync(cancellationToken);
        if (guiche is null)
        {
            return new PostoGuicheDto(unidadeId, unidade, null, null, null);
        }

        var emCurso = await (
                from a in db.Atendimentos.AsNoTracking()
                join f in db.Fichas.AsNoTracking() on a.FichaId equals f.Id
                where a.EstacaoId == guiche.Id
                    && (a.Estado == EstadoAtendimento.Chamado || a.Estado == EstadoAtendimento.EmAtendimento)
                select new { a.Id, a.FichaId, f.Letra, f.Numero, a.Estado, a.Versao })
            .FirstOrDefaultAsync(cancellationToken);
        var atendimento = emCurso is null
            ? null
            : new AtendimentoEmCursoDto(
                emCurso.Id, emCurso.FichaId, OperacaoGuiche.Senha(emCurso.Letra, emCurso.Numero), emCurso.Estado.ToString(), emCurso.Versao);
        return new PostoGuicheDto(unidadeId, unidade, guiche.Id, guiche.Nome, atendimento);
    }
}
