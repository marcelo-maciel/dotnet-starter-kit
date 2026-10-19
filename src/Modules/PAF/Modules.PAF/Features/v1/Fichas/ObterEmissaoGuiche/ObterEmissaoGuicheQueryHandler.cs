using System.Globalization;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.Features.v1.Filas;
using FSH.Modules.PAF.Features.v1.Fichas.ObterImpressaoFicha;
using FSH.Modules.PAF.Parametros;
using FSH.Modules.PAF.Sessao;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Fichas.ObterEmissaoGuiche;

/// <summary>
/// The guichê emission panel (RF11 l.364): every fila of the current unidade with its covered prioridades, the
/// fila's last call, and the last ficha this guichê emitted today. Needs a selected guichê, like the emission.
/// </summary>
public sealed class ObterEmissaoGuicheQueryHandler(
    PafDbContext db,
    SessaoCorrente sessao,
    ConcessaoCorrente concessao,
    IUnidadeCorrente unidadeCorrente,
    LeitorParametros parametros) : IQueryHandler<ObterEmissaoGuicheQuery, EmissaoGuicheDto>
{
    public async ValueTask<EmissaoGuicheDto> Handle(ObterEmissaoGuicheQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var guiche = await OperacaoGuiche.ExigirAsync(db, DonoPosto.Exigir(sessao, concessao), cancellationToken).ConfigureAwait(false);
        var unidadeId = unidadeCorrente.Exigir();
        var agora = await PafRelogio.AgoraEmSaoPauloAsync(db, cancellationToken).ConfigureAwait(false);

        var filas = await FilasAsync(guiche.Estacao, unidadeId, TimeOnly.FromDateTime(agora), cancellationToken).ConfigureAwait(false);
        var ultima = await UltimaFichaAsync(guiche.Estacao.Id, DateOnly.FromDateTime(agora), cancellationToken).ConfigureAwait(false);
        return new EmissaoGuicheDto(filas, ultima);
    }

    private async Task<List<FilaEmissaoGuicheDto>> FilasAsync(Estacao estacao, Guid unidadeId, TimeOnly hora, CancellationToken ct)
    {
        // Same emission rules as the core, read once: a button never leads to a refusal at the moment it is read
        // (the window edge is applied when the screen reads, as on the totem).
        var podeEmitir = estacao.Ativa
            && await db.Unidades.AsNoTracking().AnyAsync(u => u.Id == unidadeId && u.Ativa, ct).ConfigureAwait(false);
        var filas = await db.Filas.AsNoTracking()
            .Select(f => new { f.Id, f.NomeExibicao, f.Ativa, f.HoraInicial, f.HoraFinal, f.UltimaChamadaEm })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var cobertas = CoberturaGuiche.Pares(db);
        var prioridades = await (
                from fp in db.FilaPrioridades.AsNoTracking()
                join p in db.Prioridades.AsNoTracking() on fp.PrioridadeId equals p.Id
                where p.Ativa && cobertas.Any(c => c.FilaId == fp.FilaId && c.PrioridadeId == p.Id)
                orderby fp.Ordem, p.Nome
                select new { fp.FilaId, PrioridadeId = p.Id, p.Nome, fp.Letra })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var porFila = prioridades.ToLookup(p => p.FilaId, p => new PrioridadeEmissaoGuicheDto(p.PrioridadeId, p.Nome, p.Letra));

        // RF06 "Ordenação de filas" (C10), the totem's key. "ordem" = this guichê's own fila order (the filas it calls),
        // then the filas it does not call by name: a guichê has no emission list of its own to order by.
        var ordemDoGuiche = await db.EstacaoFilas.AsNoTracking()
            .Where(ef => ef.EstacaoId == estacao.Id && ef.Papel == PapelEstacao.Guiche)
            .Select(ef => new { ef.FilaId, ef.Ordem })
            .ToDictionaryAsync(ef => ef.FilaId, ef => ef.Ordem, ct)
            .ConfigureAwait(false);
        var ordenacao = await parametros.LerAsync(CatalogoParametros.OrdenacaoFilas, unidadeId, ct).ConfigureAwait(false);
        var porNome = StringComparer.Create(CultureInfo.GetCultureInfo("pt-BR"), CompareOptions.IgnoreCase);
        var ordenadas = ordenacao == CatalogoParametros.OrdenacaoPorNome
            ? filas.OrderBy(f => f.NomeExibicao, porNome)
            : filas.OrderBy(f => ordemDoGuiche.TryGetValue(f.Id, out var ordem) ? ordem : int.MaxValue).ThenBy(f => f.NomeExibicao, porNome);

        return ordenadas
            .Select(f =>
            {
                var foraDoHorario = !Fila.JanelaPermiteEmissao(f.HoraInicial, f.HoraFinal, hora);
                return new FilaEmissaoGuicheDto(
                    f.Id, f.NomeExibicao, f.Ativa, foraDoHorario, podeEmitir && f.Ativa && !foraDoHorario,
                    f.UltimaChamadaEm is { } chamada ? PafRelogio.EmSaoPaulo(chamada) : null,
                    porFila[f.Id].ToList());
            })
            .ToList();
    }

    private async Task<FichaEmitidaDto?> UltimaFichaAsync(Guid estacaoId, DateOnly hoje, CancellationToken ct)
    {
        // Only this guichê's manual emissions: a combined "Emissão de Fichas e Guichê" station also emits as a totem,
        // under its own credential, and "reimprimir a última" is the attendant's last ficha, not the totem's.
        var fichaId = await db.Fichas.AsNoTracking()
            .Where(f => f.EstacaoId == estacaoId && f.Canal == CanalEmissao.Guiche && f.DataOperacional == hoje)
            .OrderByDescending(f => f.CriadoEm)
            .ThenByDescending(f => f.Id)
            .Select(f => (Guid?)f.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return fichaId is { } id
            ? await ImpressaoDaFicha.LerAsync(db, parametros, id, ct).ConfigureAwait(false)
            : null;
    }
}
