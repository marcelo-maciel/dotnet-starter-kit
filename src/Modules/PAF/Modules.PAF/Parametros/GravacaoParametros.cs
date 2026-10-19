using System.Net;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Sessao;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Parametros;

/// <summary>
/// The only writer of <see cref="Parametro"/> rows (RF06). One upsert by <c>INSERT ... ON CONFLICT</c> on
/// <c>UX_Parametros_Chave</c> (Chave, UnidadeId, TenantId, NULLS NOT DISTINCT), so concurrent PUTs of the same key and
/// level end in one row whatever the interleaving. Writes of a tenant serialize on an advisory lock, because the hotkey
/// rule spans six keys and two levels: two PUTs of different keys could otherwise each pass the check and together
/// leave two actions on one key.
/// </summary>
public sealed class GravacaoParametros(
    PafDbContext db,
    AtosImpersonacao atos,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
{
    public const string MensagemValorInvalido = "Valor inválido para o parâmetro.";

    public static string MensagemTeclaRepetida(TeclaRepetida repetida)
    {
        ArgumentNullException.ThrowIfNull(repetida);
        return $"A tecla {repetida.Tecla} ficaria com duas ações do guichê ({repetida.PrimeiraChave} e {repetida.SegundaChave}).";
    }

    /// <summary>Empresa default when <paramref name="unidadeId"/> is null, else the unidade's override.</summary>
    public async Task GravarAsync(Guid? unidadeId, string chave, string valor, CancellationToken ct)
    {
        var parametro = Exigir(chave);
        var normalizado = parametro.Normalizar(valor)
            ?? throw new CustomException(MensagemValorInvalido, [$"{chave}: {Regra(parametro)}"], HttpStatusCode.BadRequest);
        var tenant = Tenant();
        var nivel = unidadeId is null ? nameof(NivelParametro.Empresa) : nameof(NivelParametro.Unidade);

        await using var transacao = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await TravarTenantAsync(tenant, ct).ConfigureAwait(false);
        if (parametro.Tipo == TipoParametro.Tecla)
        {
            await ConferirTeclasAsync(unidadeId, linhas => Substituir(linhas, unidadeId, chave, normalizado), ct).ConfigureAwait(false);
        }

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO paf."Parametros" ("Id", "UnidadeId", "Nivel", "Chave", "Valor", "TenantId")
            VALUES ({Guid.CreateVersion7()}, {unidadeId}::uuid, {nivel}, {chave}, {normalizado}, {tenant})
            ON CONFLICT ("Chave", "UnidadeId", "TenantId") DO UPDATE SET "Valor" = EXCLUDED."Valor"
            """, ct).ConfigureAwait(false);
        await atos.GravarAsync(atendimentoId: null, ct).ConfigureAwait(false);
        await transacao.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Idempotent: removing an override that does not exist leaves nothing to undo.</summary>
    public async Task RemoverDaUnidadeAsync(Guid unidadeId, string chave, CancellationToken ct)
    {
        var parametro = Exigir(chave);
        var tenant = Tenant();

        await using var transacao = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await TravarTenantAsync(tenant, ct).ConfigureAwait(false);
        if (parametro.Tipo == TipoParametro.Tecla)
        {
            await ConferirTeclasAsync(unidadeId, linhas => linhas.Where(l => !(l.UnidadeId == unidadeId && l.Chave == chave)).ToList(), ct)
                .ConfigureAwait(false);
        }

        // Tenant and unit filters apply: only the current unidade's own row can match.
        await db.Parametros
            .Where(p => p.UnidadeId == unidadeId && p.Chave == chave)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        await atos.GravarAsync(atendimentoId: null, ct).ConfigureAwait(false);
        await transacao.CommitAsync(ct).ConfigureAwait(false);
    }

    private static ParametroCatalogado Exigir(string chave) =>
        CatalogoParametros.Buscar(chave) ?? throw new NotFoundException($"Parâmetro {chave} não existe.");

    private static string Regra(ParametroCatalogado parametro) => parametro.Tipo switch
    {
        TipoParametro.Booleano => "use true ou false",
        TipoParametro.Inteiro when parametro.Chave == CatalogoParametros.QuantidadeChamadasPainel.Chave =>
            $"inteiro de 1 a {CatalogoParametros.QuantidadeMaximaChamadasPainel}",
        TipoParametro.Inteiro => "inteiro maior que zero",
        TipoParametro.Tecla => "tecla de F1 a F12",
        TipoParametro.Opcao => $"uma das opções: {string.Join(", ", parametro.Opcoes)}",
        _ => "texto sem caracteres de controle, dentro do tamanho máximo",
    };

    private string Tenant() => tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();

    private Task TravarTenantAsync(string tenant, CancellationToken ct)
    {
        var chave = $"paf-parametros:{tenant}";
        return db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({chave}, 0))", ct);
    }

    /// <summary>
    /// Checks the hotkey rule on the rows as they would be after the write. An empresa write is checked against the
    /// empresa level and against every unidade that overrides any hotkey, because a new default can collide with an
    /// override the company admin never set; a unidade write touches only that unidade.
    /// </summary>
    private async Task ConferirTeclasAsync(Guid? unidadeId, Func<List<LinhaTecla>, List<LinhaTecla>> aplicar, CancellationToken ct)
    {
        var chaves = CatalogoParametros.Teclas.Select(t => t.Chave).ToList();
        // Explicit cross-unit read (CONTEXT.md): the empresa default must hold in every unidade of the tenant.
        var atuais = await db.Parametros.AsNoTracking()
            .IgnoreQueryFilters([PafQueryFilters.Unidade])
            .Where(p => chaves.Contains(p.Chave))
            .Select(p => new LinhaTecla(p.UnidadeId, p.Chave, p.Valor))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var depois = aplicar(atuais);

        var empresa = depois.Where(l => l.UnidadeId is null).ToDictionary(l => l.Chave, l => l.Valor, StringComparer.Ordinal);
        List<Guid> unidades = unidadeId is { } alvo
            ? [alvo]
            : depois.Where(l => l.UnidadeId is not null).Select(l => l.UnidadeId!.Value).Distinct().ToList();

        var repetida = unidadeId is null
            ? ConflitoDeTeclas.Encontrar(empresa, new Dictionary<string, string>(StringComparer.Ordinal))
            : null;
        foreach (var unidade in unidades)
        {
            repetida ??= ConflitoDeTeclas.Encontrar(empresa,
                depois.Where(l => l.UnidadeId == unidade).ToDictionary(l => l.Chave, l => l.Valor, StringComparer.Ordinal));
        }

        if (repetida is not null)
        {
            throw new CustomException(MensagemValorInvalido, [MensagemTeclaRepetida(repetida)], HttpStatusCode.BadRequest);
        }
    }

    private static List<LinhaTecla> Substituir(List<LinhaTecla> linhas, Guid? unidadeId, string chave, string valor) =>
        [.. linhas.Where(l => !(l.UnidadeId == unidadeId && l.Chave == chave)), new LinhaTecla(unidadeId, chave, valor)];

    private sealed record LinhaTecla(Guid? UnidadeId, string Chave, string Valor);
}
