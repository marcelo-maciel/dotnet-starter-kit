using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Midias;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estacoes;

/// <summary>
/// C17 (RF04 blocos Emissão e Painel, D16): the background image a station block points at. The contrast itself is
/// checked by <see cref="ConfiguracaoDaEstacao.Validar"/> against the sample; this class checks the mídia behind it and,
/// for the screens, signs its read URL (M04).
/// </summary>
internal static class ImagemDeFundoDaEstacao
{
    // ponytail: the luminance sample is measured by the cadastro in the browser (canvas), because no image decoder is
    // available to the API (none in Directory.Packages.props, which this module may not edit). The server trusts it
    // from an Estacoes.Create/Update holder and binds it to the mídia version and file. Upgrade: decode on the server
    // (e.g. SkiaSharp, MIT) at the M03 confirmation and drop the sample from the request.

    /// <summary>
    /// The exhibition a background URL must cover. The screens re-read the configuration every 60 s, and M04 serves a
    /// cached URL for validity − exhibition: with this value a URL handed out at the end of the cache window still
    /// lives 10 minutes, ten re-reads.
    /// </summary>
    public static readonly TimeSpan ExibicaoDoFundo = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 400 unless each image is an active <see cref="TipoMidia.Imagem"/> of the current unidade (the read goes through the
    /// unit filter: another unidade or tenant is "not found") with a confirmed file, at the version the sample was
    /// measured on. Returns the mídia's current envio per mídia, which <see cref="ConfiguracaoDaEstacao.Serializar"/> stores.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, Guid>> ConferirAsync(PafDbContext db, BlocosEstacaoDto configuracao, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(configuracao);
        var pedidas = new List<(string Bloco, ImagemFundoDto Imagem)>();
        if (configuracao.Emissao?.ImagemFundo is { } daEmissao)
        {
            pedidas.Add(("Emissao", daEmissao));
        }

        if (configuracao.Painel?.ImagemFundo is { } doPainel)
        {
            pedidas.Add(("Painel", doPainel));
        }

        if (pedidas.Count == 0)
        {
            return new Dictionary<Guid, Guid>();
        }

        var ids = pedidas.Select(p => p.Imagem.MidiaId).Distinct().ToList();
        var midias = await db.Midias.AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .Select(m => new
            {
                m.Id,
                m.Tipo,
                m.Ativa,
                m.Versao,
                m.EnvioId,
                Confirmado = db.EnviosMidia.Any(e => e.Id == m.EnvioId && e.ChaveObjeto == m.ChaveObjeto
                    && e.ConfirmadoEm != null && e.RecusadoEm == null && e.RemovidoEm == null),
            })
            .ToDictionaryAsync(m => m.Id, ct)
            .ConfigureAwait(false);

        var erros = new List<string>();
        foreach (var (bloco, imagem) in pedidas)
        {
            if (!midias.TryGetValue(imagem.MidiaId, out var m))
            {
                erros.Add($"{bloco}.ImagemFundo: mídia não encontrada nesta unidade.");
            }
            else if (m.Tipo != TipoMidia.Imagem)
            {
                erros.Add($"{bloco}.ImagemFundo: a mídia precisa ser uma imagem ({FormatosDeImagem()}).");
            }
            else if (!m.Ativa || !m.Confirmado)
            {
                erros.Add($"{bloco}.ImagemFundo: a mídia está inativa ou sem arquivo confirmado.");
            }
            else if (m.Versao != imagem.VersaoMidia)
            {
                erros.Add($"{bloco}.ImagemFundo: a mídia mudou desde a medição da imagem; meça de novo antes de salvar.");
            }
        }

        if (erros.Count > 0)
        {
            throw new CustomException("Configuração da estação inválida.", erros, HttpStatusCode.BadRequest);
        }

        return midias.ToDictionary(m => m.Key, m => m.Value.EnvioId);
    }

    /// <summary>
    /// Read URL of a stored background for the screens; null when there is none, the mídia left the unidade, its file
    /// was swapped after the measurement (the sample no longer describes it) or M04 refuses it (inactive, unconfirmed).
    /// </summary>
    public static async Task<UrlLeituraMidiaDto?> UrlAsync(
        PafDbContext db, UrlLeituraMidia urlLeitura, ImagemFundoGravada? imagem, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(urlLeitura);
        if (imagem is null)
        {
            return null;
        }

        var midia = await db.Midias.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == imagem.MidiaId && m.EnvioId == imagem.EnvioId, ct)
            .ConfigureAwait(false);
        return midia is null ? null : await urlLeitura.GerarAsync(midia, ExibicaoDoFundo, ct).ConfigureAwait(false);
    }

    private static string FormatosDeImagem() =>
        string.Join(", ", FormatosMidia.Aceitos.Where(f => f.Tipo == TipoMidia.Imagem).Select(f => f.Extensao));
}
