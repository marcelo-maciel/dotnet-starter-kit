using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.v1.Guiches;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Features.v1.Sessao.SetGuiche;
using FSH.Modules.PAF.Sessao;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Guiches.ListGuichesDaUnidade;

/// <summary>
/// What the guichê selection offers (seção 5): active guichês of the current unit, from the unit filter, each
/// with the user holding it and the senha of another user's pendência, which the selection offers to release.
/// Only the user name leaves, the same text the selection refusal already shows.
/// </summary>
public sealed class ListGuichesDaUnidadeQueryHandler(PafDbContext db, SessaoCorrente sessao, ConcessaoCorrente concessao, ISessionService sessoes)
    : IQueryHandler<ListGuichesDaUnidadeQuery, IReadOnlyList<GuicheDaUnidadeDto>>
{
    public const string OcupanteSuporte = "suporte";
    public const string OcupanteSemNome = "outro usuário";

    public async ValueTask<IReadOnlyList<GuicheDaUnidadeDto>> Handle(ListGuichesDaUnidadeQuery query, CancellationToken cancellationToken)
    {
        // S11: under impersonation "meu" is the grant's own link, and a pendência of the impersonated user is his to resume.
        var dono = DonoPosto.Exigir(sessao, concessao);
        var usuarioId = dono.UsuarioId;
        var guiches = await db.Estacoes.AsNoTracking()
            .Where(e => e.Ativa && SetGuicheCommandHandler.TiposComGuiche.Contains(e.Tipo))
            .OrderBy(e => e.Nome)
            .Select(e => new { e.Id, e.Nome, e.Tipo })
            .ToListAsync(cancellationToken);
        var ids = guiches.Select(g => g.Id).ToList();
        var vinculos = await db.SessoesEstacao.AsNoTracking()
            .Where(v => ids.Contains(v.EstacaoId) && v.LiberadoEm == null)
            .ToDictionaryAsync(v => v.EstacaoId, cancellationToken);

        var resultado = new List<GuicheDaUnidadeDto>(guiches.Count);
        foreach (var g in guiches)
        {
            vinculos.TryGetValue(g.Id, out var vinculo);
            var meu = vinculo is not null && dono.Possui(vinculo);
            var ocupante = vinculo is null || meu ? null : await OcupanteAsync(vinculo.SessaoId, cancellationToken);
            var pendencia = await SetGuicheCommandHandler.PendenciaDeOutroUsuarioAsync(db, g.Id, usuarioId, cancellationToken);
            resultado.Add(new GuicheDaUnidadeDto(g.Id, g.Nome, g.Tipo.ToString(), ocupante, meu, pendencia?.Senha));
        }

        return resultado;
    }

    // ponytail: one session lookup per occupied guichê and one pendência read per guichê; a unit has a handful.
    // Batch both (Identity, one grouped query) if that grows.
    private async Task<string> OcupanteAsync(Guid? sessaoDona, CancellationToken ct)
    {
        if (sessaoDona is not { } dona)
        {
            return OcupanteSuporte;
        }

        var dto = await sessoes.GetSessionAsync(dona, ct);
        return dto?.UserName ?? OcupanteSemNome;
    }
}
