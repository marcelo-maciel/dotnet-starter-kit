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
/// The guichê is read with the unit filter: a link to another unit's station is not this unit's post. Only the
/// caller's own atendimento is "em curso" here: another user's pendência is never shown as his (seção 5).
/// </summary>
public sealed class ObterPostoGuicheQueryHandler(PafDbContext db, SessaoCorrente sessao, ConcessaoCorrente concessao, IUnidadeCorrente unidadeCorrente)
    : IQueryHandler<ObterPostoGuicheQuery, PostoGuicheDto>
{
    public async ValueTask<PostoGuicheDto> Handle(ObterPostoGuicheQuery query, CancellationToken cancellationToken)
    {
        // S11: under impersonation the post is the grant's own link; the user is the impersonated one (sub).
        var dono = DonoPosto.Exigir(sessao, concessao);
        var usuarioId = dono.UsuarioId;
        if (unidadeCorrente.UnidadeId is not { } unidadeId)
        {
            return new PostoGuicheDto(null, null, null, null, null);
        }

        var unidade = await db.Unidades.AsNoTracking()
            .Where(u => u.Id == unidadeId)
            .Select(u => u.Nome)
            .FirstOrDefaultAsync(cancellationToken);
        var vinculo = await VinculosGuiche.AtivoDoDonoAsync(db, dono, cancellationToken);
        var guiche = vinculo is null
            ? null
            : await db.Estacoes.AsNoTracking()
                .Where(e => e.Id == vinculo.EstacaoId)
                .Select(e => new { e.Id, e.Nome })
                .FirstOrDefaultAsync(cancellationToken);
        if (guiche is null)
        {
            return new PostoGuicheDto(unidadeId, unidade, null, null, null);
        }

        var emCurso = await (
                from a in db.Atendimentos.AsNoTracking()
                join f in db.Fichas.AsNoTracking() on a.FichaId equals f.Id
                where a.EstacaoId == guiche.Id
                    && a.UsuarioId == usuarioId
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
