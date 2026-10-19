using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.v1.Estatistica;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Features.v1.Sessao.SetGuiche;
using Microsoft.EntityFrameworkCore;
using static FSH.Modules.PAF.Features.v1.Estatistica.Atendimentos.EstatisticaAtendimentos;

namespace FSH.Modules.PAF.Features.v1.Estatistica.Atendimentos;

/// <summary>One row of types 1 to 4: the realised atendimentos of one unidade and one fila, guichê or atendente.</summary>
internal sealed record GrupoDeTempos(Guid UnidadeId, Guid? Chave, string Rotulo, Agregado Agregado);

/// <summary>Columns and cells shared by types 1 to 4: the three RF07 metrics, in the order RF07 lists them.</summary>
internal static class TemposDeAtendimento
{
    public static ResultadoEstatisticaDto Tabela(
        TipoEstatistica tipo, ColunaEstatisticaDto colunaGrupo, IReadOnlyList<GrupoDeTempos> grupos, Dictionary<Guid, string> unidades) =>
        EstatisticaAtendimentos.Tabela(
            tipo,
            [ColunaUnidade, colunaGrupo, ColunaEspera, ColunaAtendimento, ColunaTotal],
            grupos,
            g => g.UnidadeId,
            g => g.Rotulo,
            unidades,
            Celulas);

    public static List<GrupoDeTempos> Agrupar(IEnumerable<Medida> medidas, Func<Medida, Guid?> chaveDe, Func<Guid?, string> rotuloDe) =>
        [.. medidas
            .GroupBy(m => (m.UnidadeId, Chave: chaveDe(m)))
            .Select(g => new GrupoDeTempos(g.Key.UnidadeId, g.Key.Chave, rotuloDe(g.Key.Chave), Agregado.De(g)))];

    public static IOrderedEnumerable<GrupoDeTempos> PorUnidade(IEnumerable<GrupoDeTempos> grupos, Dictionary<Guid, string> unidades) =>
        grupos.OrderBy(g => NomeDaUnidade(unidades, g.UnidadeId), StringComparer.CurrentCulture);

    private static IReadOnlyList<CelulaEstatisticaDto> Celulas(IReadOnlyList<GrupoDeTempos> grupos)
    {
        var agregado = Agregado.Somar(grupos.Select(g => g.Agregado));
        return [Numero(agregado.MediaEspera), Numero(agregado.MediaAtendimento), Numero(agregado.Quantidade)];
    }
}

/// <summary>Types 1 and 2 (D08): one row per fila with the three metrics; the two types differ only in the ranking metric.</summary>
internal static class TemposPorFila
{
    public static async Task<(List<GrupoDeTempos> Grupos, Dictionary<Guid, string> Unidades)> MedirAsync(
        PafDbContext db, FiltroEstatistica filtro, CancellationToken ct)
    {
        var medidas = await MedirRealizadosAsync(db, filtro, ct).ConfigureAwait(false);
        var unidades = await NomesDasUnidadesAsync(db, filtro, ct).ConfigureAwait(false);
        var filas = await NomesDasFilasAsync(db, medidas.Select(m => m.FilaId), ct).ConfigureAwait(false);
        var grupos = TemposDeAtendimento.Agrupar(medidas, m => m.FilaId, chave => filas[chave!.Value]);
        return (grupos, unidades);
    }
}

public sealed class ConsultaAtendimentosPorTempoDeEspera(PafDbContext db) : IConsultaEstatistica
{
    public TipoEstatistica Tipo => TipoEstatistica.AtendimentosPorTempoDeEspera;

    public async Task<ResultadoEstatisticaDto> ConsultarAsync(FiltroEstatistica filtro, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        var (grupos, unidades) = await TemposPorFila.MedirAsync(db, filtro, cancellationToken).ConfigureAwait(false);
        var ordenados = TemposDeAtendimento.PorUnidade(grupos, unidades)
            .ThenByDescending(g => g.Agregado.MediaEspera)
            .ThenBy(g => g.Rotulo, StringComparer.CurrentCulture)
            .ToList();
        return TemposDeAtendimento.Tabela(Tipo, ColunaFila, ordenados, unidades);
    }
}

public sealed class ConsultaAtendimentosPorTempoDeAtendimento(PafDbContext db) : IConsultaEstatistica
{
    public TipoEstatistica Tipo => TipoEstatistica.AtendimentosPorTempoDeAtendimento;

    public async Task<ResultadoEstatisticaDto> ConsultarAsync(FiltroEstatistica filtro, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        var (grupos, unidades) = await TemposPorFila.MedirAsync(db, filtro, cancellationToken).ConfigureAwait(false);
        var ordenados = TemposDeAtendimento.PorUnidade(grupos, unidades)
            .ThenByDescending(g => g.Agregado.MediaAtendimento)
            .ThenBy(g => g.Rotulo, StringComparer.CurrentCulture)
            .ToList();
        return TemposDeAtendimento.Tabela(Tipo, ColunaFila, ordenados, unidades);
    }
}

/// <summary>
/// Type 3: every guichê station of the consulted unidades, deactivated ones included (their past atendimentos
/// still count), with zeros unless <see cref="FiltroEstatistica.SomenteEstacoesComAtendimento"/>. A station of
/// another type that closed an atendimento also gets its row, so the subtotals always add up to the total.
/// </summary>
public sealed class ConsultaAtendimentosPorGuiche(PafDbContext db) : IConsultaEstatistica
{
    public const string RotuloSemGuiche = "Sem guichê";

    public static readonly ColunaEstatisticaDto ColunaGuiche = new("guiche", "Guichê", TipoColunaEstatistica.Texto);

    public TipoEstatistica Tipo => TipoEstatistica.AtendimentosPorGuiche;

    public async Task<ResultadoEstatisticaDto> ConsultarAsync(FiltroEstatistica filtro, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        var medidas = await MedirRealizadosAsync(db, filtro, cancellationToken).ConfigureAwait(false);
        var unidades = await NomesDasUnidadesAsync(db, filtro, cancellationToken).ConfigureAwait(false);
        var ids = filtro.Unidades.ToArray();
        var comAtendimento = medidas.Where(m => m.EstacaoId is not null).Select(m => m.EstacaoId!.Value).Distinct().ToArray();
        var estacoes = await EstacoesSemFiltroDeUnidade(db)
            .Where(e => ids.Contains(e.UnidadeId) && (SetGuicheCommandHandler.TiposComGuiche.Contains(e.Tipo) || comAtendimento.Contains(e.Id)))
            .Select(e => new { e.Id, e.UnidadeId, e.Nome })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var medidos = TemposDeAtendimento.Agrupar(
            medidas, m => m.EstacaoId, chave => chave is { } id ? estacoes.Single(e => e.Id == id).Nome : RotuloSemGuiche);
        var semAtendimento = estacoes
            .Where(e => !medidos.Any(g => g.Chave == e.Id))
            .Select(e => new GrupoDeTempos(e.UnidadeId, e.Id, e.Nome, new Agregado(0, 0, 0)));
        var grupos = TemposDeAtendimento.PorUnidade(medidos.Concat(semAtendimento), unidades)
            .ThenBy(g => g.Rotulo, StringComparer.CurrentCulture)
            .Where(g => !filtro.SomenteEstacoesComAtendimento || g.Agregado.Quantidade > 0)
            .ToList();
        return TemposDeAtendimento.Tabela(Tipo, ColunaGuiche, grupos, unidades);
    }
}

/// <summary>
/// Type 4 (D10, under Estatisticas.ViewPorAtendente): one row per atendente. Names come from the Identity
/// contract, never from a JOIN into the identity schema.
/// </summary>
public sealed class ConsultaAtendimentosPorAtendente(PafDbContext db, IUserProfileService usuarios) : IConsultaEstatistica
{
    public const string RotuloSemAtendente = "Sem atendente";
    public const string RotuloUsuarioRemovido = "Usuário removido";

    public static readonly ColunaEstatisticaDto ColunaAtendente = new("atendente", "Atendente", TipoColunaEstatistica.Texto);

    public TipoEstatistica Tipo => TipoEstatistica.AtendimentosPorAtendente;

    public async Task<ResultadoEstatisticaDto> ConsultarAsync(FiltroEstatistica filtro, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        var medidas = await MedirRealizadosAsync(db, filtro, cancellationToken).ConfigureAwait(false);
        var unidades = await NomesDasUnidadesAsync(db, filtro, cancellationToken).ConfigureAwait(false);

        // One read of the tenant's users instead of one GetAsync per atendente: GetAsync throws for a removed
        // user, and a removed atendente must still show his past atendimentos.
        // ponytail: reads every user of the tenant; ask Identity for a by-ids lookup if a tenant reaches thousands.
        var nomes = medidas.Any(m => m.UsuarioId is not null)
            ? (await usuarios.GetListAsync(cancellationToken).ConfigureAwait(false))
                .Where(u => u.Id is not null)
                .ToDictionary(u => u.Id!, Nome, StringComparer.OrdinalIgnoreCase)
            : [];

        var grupos = TemposDeAtendimento.Agrupar(medidas, m => m.UsuarioId, chave => Rotulo(chave, nomes));
        var ordenados = TemposDeAtendimento.PorUnidade(grupos, unidades)
            .ThenBy(g => g.Rotulo, StringComparer.CurrentCulture)
            .ToList();
        return TemposDeAtendimento.Tabela(Tipo, ColunaAtendente, ordenados, unidades);
    }

    private static string Rotulo(Guid? usuarioId, Dictionary<string, string> nomes)
    {
        if (usuarioId is not { } id)
        {
            return RotuloSemAtendente;
        }

        return nomes.TryGetValue(id.ToString(), out var nome) ? nome : RotuloUsuarioRemovido;
    }

    private static string Nome(FSH.Modules.Identity.Contracts.DTOs.UserDto usuario)
    {
        var completo = $"{usuario.FirstName} {usuario.LastName}".Trim();
        return completo.Length > 0 ? completo : usuario.UserName ?? RotuloUsuarioRemovido;
    }
}
