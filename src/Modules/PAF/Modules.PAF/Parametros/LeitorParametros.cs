using FSH.Modules.PAF.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.Parametros;

/// <summary>
/// Effective value of an RF06 parameter for a unidade: the unidade row, else the empresa row (UnidadeId NULL),
/// else the catalog default. Reads through the tenant and unit filters of <see cref="PafDbContext"/>, so the
/// unidade asked for must be the current one; another unidade's row is never visible.
/// </summary>
public sealed partial class LeitorParametros(PafDbContext db, ILogger<LeitorParametros> logger)
{
    public async Task<T> LerAsync<T>(DefinicaoParametro<T> definicao, Guid unidadeId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definicao);
        var linhas = await db.Parametros.AsNoTracking()
            .Where(p => p.Chave == definicao.Chave && (p.UnidadeId == unidadeId || p.UnidadeId == null))
            .Select(p => new { p.UnidadeId, p.Valor })
            .ToListAsync(ct);

        // UX_Parametros_Chave allows at most one row per level, so the unidade row, when present, is the only non-null one.
        var linha = linhas.Find(l => l.UnidadeId is not null) ?? linhas.Find(l => l.UnidadeId is null);
        if (linha is null)
        {
            return definicao.Padrao;
        }

        var (valido, valor) = definicao.Interpretar(linha.Valor);
        if (valido)
        {
            return valor;
        }

        // Absent is the designed default and stays silent; an unreadable stored value is a cadastro error worth seeing.
        LogValorInvalido(logger, definicao.Chave, linha.UnidadeId is null ? "Empresa" : "Unidade", unidadeId);
        return definicao.Padrao;
    }

    /// <summary>
    /// Every row of the unidade and of the empresa in one query, for a caller that reads several keys (the emission,
    /// the parameters screen). Same filters and the same resolution as <see cref="LerAsync{T}"/>.
    /// </summary>
    public async Task<ParametrosDaUnidade> CarregarAsync(Guid unidadeId, CancellationToken ct)
    {
        var linhas = await db.Parametros.AsNoTracking()
            .Where(p => p.UnidadeId == unidadeId || p.UnidadeId == null)
            .Select(p => new { p.UnidadeId, p.Chave, p.Valor })
            .ToListAsync(ct);

        return new ParametrosDaUnidade(
            linhas.Where(l => l.UnidadeId is null).ToDictionary(l => l.Chave, l => l.Valor, StringComparer.Ordinal),
            linhas.Where(l => l.UnidadeId is not null).ToDictionary(l => l.Chave, l => l.Valor, StringComparer.Ordinal),
            (chave, origem) => LogValorInvalido(logger, chave, origem.ToString(), unidadeId));
    }

    [LoggerMessage(EventId = 9060,Level = LogLevel.Warning,
        Message = "Parametro com valor invalido, usando o padrao; chave={Chave} nivel={Nivel} unidade={UnidadeId}")]
    private static partial void LogValorInvalido(ILogger logger, string chave, string nivel, Guid unidadeId);
}
