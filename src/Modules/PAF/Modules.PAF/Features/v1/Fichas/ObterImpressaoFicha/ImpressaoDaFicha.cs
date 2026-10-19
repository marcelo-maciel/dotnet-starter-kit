using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.Parametros;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Fichas.ObterImpressaoFicha;

/// <summary>
/// The stored ficha as printed (RF11 "reimprimir", RF12): read only, so the number, the senha and the
/// <c>IdentificadorPublico</c> are the emission's. Single reader for every reprint path (fichas do dia, the
/// guichê's last ficha, the reprint by senha). Unit-filtered reads: a ficha of another unidade is null.
/// </summary>
internal static class ImpressaoDaFicha
{
    public static async Task<FichaEmitidaDto?> LerAsync(PafDbContext db, LeitorParametros parametros, Guid fichaId, CancellationToken ct)
    {
        var ficha = await db.Fichas.AsNoTracking().SingleOrDefaultAsync(f => f.Id == fichaId, ct).ConfigureAwait(false);
        if (ficha is null)
        {
            return null;
        }

        // The emission's atendimento: forwards add rows with an origin, the printed ficha stays the first one.
        var atendimentoId = await db.Atendimentos.AsNoTracking()
            .Where(a => a.FichaId == ficha.Id && a.AtendimentoOrigemId == null)
            .Select(a => a.Id)
            .SingleAsync(ct)
            .ConfigureAwait(false);
        var unidade = await db.Unidades.AsNoTracking().Where(u => u.Id == ficha.UnidadeId).Select(u => u.Nome).SingleAsync(ct).ConfigureAwait(false);
        var fila = await db.Filas.AsNoTracking().Where(f => f.Id == ficha.FilaId).Select(f => f.NomeExibicao).SingleAsync(ct).ConfigureAwait(false);
        var prioridade = await db.Prioridades.AsNoTracking().Where(p => p.Id == ficha.PrioridadeId).Select(p => p.Nome).SingleAsync(ct).ConfigureAwait(false);

        // RF06 "Parâmetros das fichas" (C10) as they are now, like the emission and its replay: only the number,
        // the senha and the IdentificadorPublico are frozen at emission.
        var impressao = await parametros.CarregarAsync(ficha.UnidadeId, ct).ConfigureAwait(false);
        return new FichaEmitidaDto(ficha.Id, atendimentoId, ficha.IdentificadorPublico, OperacaoGuiche.Senha(ficha.Letra, ficha.Numero),
            ficha.Letra, ficha.Numero, unidade, fila, prioridade, PafRelogio.EmSaoPaulo(ficha.CriadoEm),
            impressao.Ler(CatalogoParametros.TituloFichas),
            impressao.Ler(CatalogoParametros.MensagemRodapeFicha),
            impressao.Ler(CatalogoParametros.ImprimirQrFicha));
    }
}
