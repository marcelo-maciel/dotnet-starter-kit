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
/// with the user holding it. Only the user name leaves, the same text the selection refusal already shows.
/// </summary>
public sealed class ListGuichesDaUnidadeQueryHandler(PafDbContext db, SessaoCorrente sessao, ISessionService sessoes)
    : IQueryHandler<ListGuichesDaUnidadeQuery, IReadOnlyList<GuicheDaUnidadeDto>>
{
    public const string OcupanteSuporte = "suporte";
    public const string OcupanteSemNome = "outro usuário";

    public async ValueTask<IReadOnlyList<GuicheDaUnidadeDto>> Handle(ListGuichesDaUnidadeQuery query, CancellationToken cancellationToken)
    {
        var (_, sessaoId, _) = sessao.Exigir();
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
            var meu = vinculo is not null && vinculo.SessaoId == sessaoId;
            var ocupante = vinculo is null || meu ? null : await OcupanteAsync(vinculo.SessaoId, cancellationToken);
            resultado.Add(new GuicheDaUnidadeDto(g.Id, g.Nome, g.Tipo.ToString(), ocupante, meu));
        }

        return resultado;
    }

    // ponytail: one session lookup per occupied guichê; a unit has a handful. Batch through Identity if that grows.
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
