using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Midias;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Parametros;

/// <summary>
/// C19, RF06 "Logotipo": the signed read URL of the effective logo of a unidade, or null for the system logo (no row,
/// or the chosen mídia is now inactive or without a confirmed file: the screen falls back instead of showing a hole).
/// </summary>
public sealed class LogotipoEfetivo(PafDbContext db, UrlLeituraMidia leitura)
{
    /// <summary>
    /// The totem and the painel re-read their configuration every 60 s, and a ficha prints at once, so one minute is the
    /// longest a given URL is shown; <see cref="UrlLeituraMidia.Validade"/> raises it to the read floor, which keeps one
    /// URL (and the browser's cached copy) for about an hour.
    /// </summary>
    public static readonly TimeSpan ExibicaoEntreReleituras = TimeSpan.FromMinutes(1);

    public async Task<UrlLeituraMidiaDto?> LerAsync(ParametrosDaUnidade parametros, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parametros);
        var valor = parametros.Ler(CatalogoParametros.Logotipo);
        if (valor.Length == 0)
        {
            return null;
        }

        var midiaId = Guid.Parse(valor);
        // Explicit cross-unit read (CONTEXT.md): an empresa logo is a mídia of the unidade where the company admin
        // uploaded it, and must show in every unidade of the tenant. The tenant filter still applies; the write
        // (GravacaoParametros) admitted only an image of the writer's unidade.
        var midia = await db.Midias.AsNoTracking()
            .IgnoreQueryFilters([PafQueryFilters.Unidade])
            .FirstOrDefaultAsync(m => m.Id == midiaId && m.Tipo == TipoMidia.Imagem, ct)
            .ConfigureAwait(false);
        return midia is null ? null : await leitura.GerarAsync(midia, ExibicaoEntreReleituras, ct).ConfigureAwait(false);
    }
}
