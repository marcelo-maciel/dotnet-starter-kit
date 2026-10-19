using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Contracts.v1.Painel;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Midias;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Painel.ObterMidiasPainel;

/// <summary>
/// What the painel plays (M06, RF21 amended, D12): its own list in order and the unidade's programações of today, both
/// read through the unit filter of the painel credential, with the database clock. Every item carries a read URL (M04)
/// valid for its whole exhibition; an item without one (inactive mídia, file not confirmed or removed) is dropped, so
/// the painel never shows an error to the public. Nothing to play = 200 with both lists empty.
/// </summary>
public sealed class ObterMidiasPainelQueryHandler(PafDbContext db, UrlLeituraMidia urlLeitura)
    : IQueryHandler<ObterMidiasPainelQuery, MidiasPainelDto>
{
    public async ValueTask<MidiasPainelDto> Handle(ObterMidiasPainelQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var agenda = await AgendaMidia.DeHojeAsync(db, cancellationToken).ConfigureAwait(false);
        var itens = await db.PainelMidias.AsNoTracking()
            .Where(p => p.EstacaoId == query.EstacaoId)
            .OrderBy(p => p.Ordem)
            .Select(p => new { p.Id, p.MidiaId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var ids = itens.Select(i => i.MidiaId).Concat(agenda.Programacoes.Select(p => p.MidiaId)).Distinct().ToList();
        var midias = await db.Midias.AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, cancellationToken)
            .ConfigureAwait(false);

        // The same mídia repeats in a loop and across programações; one URL per (mídia, exhibition) is enough.
        var urls = new Dictionary<(Guid, long), UrlLeituraMidiaDto?>();
        async Task<UrlLeituraMidiaDto?> UrlAsync(Midia midia, long exibicaoSegundos)
        {
            if (!urls.TryGetValue((midia.Id, exibicaoSegundos), out var url))
            {
                url = await urlLeitura.GerarAsync(midia, TimeSpan.FromSeconds(exibicaoSegundos), cancellationToken).ConfigureAwait(false);
                urls[(midia.Id, exibicaoSegundos)] = url;
            }

            return url;
        }

        var lista = new List<ItemMidiaPainelDto>(itens.Count);
        foreach (var item in itens)
        {
            if (midias.TryGetValue(item.MidiaId, out var midia) && await UrlAsync(midia, midia.DuracaoSegundos).ConfigureAwait(false) is { } url)
            {
                lista.Add(new ItemMidiaPainelDto(item.Id, midia.Id, midia.Tipo.ToString(), midia.DuracaoSegundos, url.Url, url.ExpiraEm));
            }
        }

        var programacoes = new List<ProgramacaoPainelDto>(agenda.Programacoes.Count);
        foreach (var programacao in AgendaMidia.EmOrdemDePrecedencia(agenda.Programacoes))
        {
            if (midias.TryGetValue(programacao.MidiaId, out var midia)
                && await UrlAsync(midia, programacao.TempoTotalSegundos).ConfigureAwait(false) is { } url)
            {
                programacoes.Add(new ProgramacaoPainelDto(
                    programacao.Id, midia.Id, midia.Tipo.ToString(), programacao.TempoMidiaSegundos, programacao.Repeticoes,
                    (int)AgendaMidia.InicioSegundos(programacao),
                    (int)Math.Min(programacao.HoraFinalSegundos, AgendaMidia.SegundosNoDia),
                    url.Url, url.ExpiraEm));
            }
        }

        return new MidiasPainelDto(PafRelogio.ComFusoDeSaoPaulo(agenda.AgoraSaoPaulo), lista, programacoes);
    }
}
