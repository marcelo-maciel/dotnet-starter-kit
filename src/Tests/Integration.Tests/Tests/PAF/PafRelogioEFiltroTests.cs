using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Dates derived by PostgreSQL in America/Sao_Paulo (seção 4.2) and the named unit filter, fail-closed
/// when the request has no current unidade (ADR 0003).
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafRelogioEFiltroTests : IAsyncLifetime
{
    private readonly FshWebApplicationFactory _factory;
    private readonly PafBanco _banco;

    public PafRelogioEFiltroTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _banco = new PafBanco(factory);
    }

    public Task InitializeAsync() => _banco.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // 02:00Z is still 23:00 of the previous day in São Paulo (UTC-3, no DST since 2019): a UTC cut
    // would give 2026-01-01, so these cases tell the two zones apart on every run.
    [Theory]
    [InlineData("2026-01-01T02:59:59Z", "2025-12-31")]
    [InlineData("2026-01-01T03:00:00Z", "2026-01-01")]
    public async Task DataOperacionalDaFicha_EhODiaDeSaoPauloDoInstanteDeCriacao(string criadoEmUtc, string esperado)
    {
        var c = await _banco.CriarCadastroAsync();
        var fichaId = Guid.NewGuid();
        var criadoEm = DateTimeOffset.Parse(criadoEmUtc, System.Globalization.CultureInfo.InvariantCulture);

        using var scope = _factory.Services.CreateScope();
        var db = _banco.Contexto(scope, c.UnidadeId);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO paf."Fichas" ("Id", "UnidadeId", "FilaId", "PrioridadeId", "Letra", "Numero",
                "IdentificadorPublico", "Canal", "EstacaoId", "ChaveIdempotencia", "CriadoEm", "TenantId")
            VALUES ({fichaId}, {c.UnidadeId}, {c.FilaId}, {c.PrioridadeId}, 'A', 1,
                {Guid.NewGuid()}, 'Totem', {c.GuicheA}, {fichaId.ToString("N")}, {criadoEm}, {TestConstants.RootTenantId})
            """);

        var ficha = await db.Fichas.AsNoTracking().SingleAsync(f => f.Id == fichaId);
        ficha.DataOperacional.ShouldBe(DateOnly.Parse(esperado, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Emissao_CriadoEmVemDoRelogioDoBanco_EDataOperacionalDerivaDele()
    {
        var c = await _banco.CriarCadastroAsync();
        var (ficha, atendimento) = await _banco.EmitirAsync(c, 1);

        using var scope = _factory.Services.CreateScope();
        var db = _banco.Contexto(scope, c.UnidadeId);
        var agoraNoBanco = await db.Database.SqlQuery<DateTimeOffset>($"SELECT now() AS \"Value\"").SingleAsync();
        var gravada = await db.Fichas.AsNoTracking().SingleAsync(f => f.Id == ficha.Id);
        var gravado = await db.Atendimentos.AsNoTracking().SingleAsync(a => a.Id == atendimento.Id);

        gravada.CriadoEm.ShouldNotBe(default);
        (agoraNoBanco - gravada.CriadoEm).ShouldBeLessThan(TimeSpan.FromMinutes(1));
        gravada.DataOperacional.ShouldBe(DiaEmSaoPaulo(gravada.CriadoEm));
        gravado.ElegivelDesde.ShouldBe(gravado.CriadoEm);
        gravado.DataElegibilidade.ShouldBe(gravado.DataOperacional);
        gravado.Estado.ShouldBe(EstadoAtendimento.Aguardando);
        gravado.Versao.ShouldBe(0);
    }

    [Fact]
    public async Task Liberacao_ReescreveDataElegibilidadeComOMarco_ENuncaADataOperacional()
    {
        var c = await _banco.CriarCadastroAsync();
        var (_, atendimento) = await _banco.EmitirAsync(c, 1);

        using var scope = _factory.Services.CreateScope();
        var db = _banco.Contexto(scope, c.UnidadeId);
        var antes = await db.Atendimentos.AsNoTracking().SingleAsync(a => a.Id == atendimento.Id);

        var liberadoEm = new DateTimeOffset(2026, 1, 1, 2, 59, 59, TimeSpan.Zero);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE paf."Atendimentos" SET "ElegivelDesde" = {liberadoEm} WHERE "Id" = {atendimento.Id}
            """);

        var depois = await db.Atendimentos.AsNoTracking().SingleAsync(a => a.Id == atendimento.Id);
        depois.DataElegibilidade.ShouldBe(new DateOnly(2025, 12, 31));
        depois.DataOperacional.ShouldBe(antes.DataOperacional);
        depois.CriadoEm.ShouldBe(antes.CriadoEm);
    }

    [Fact]
    public async Task FiltroDeUnidade_SemUnidadeCorrente_NaoDevolveNada()
    {
        var c = await _banco.CriarCadastroAsync();
        var (ficha, atendimento) = await _banco.EmitirAsync(c, 1);

        using var scope = _factory.Services.CreateScope();
        var db = _banco.Contexto(scope, unidadeCorrente: null);

        (await db.Fichas.AsNoTracking().CountAsync(f => f.Id == ficha.Id)).ShouldBe(0);
        (await db.Atendimentos.AsNoTracking().CountAsync(a => a.Id == atendimento.Id)).ShouldBe(0);
        (await db.Filas.AsNoTracking().CountAsync(f => f.Id == c.FilaId)).ShouldBe(0);
        (await db.Estacoes.AsNoTracking().CountAsync(e => e.UnidadeId == c.UnidadeId)).ShouldBe(0);

        // Proves the rows exist and that only the unit filter hid them: the tenant filter stays on.
        (await db.Fichas.AsNoTracking().IgnoreQueryFilters([PafQueryFilters.Unidade]).CountAsync(f => f.Id == ficha.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task FiltroDeUnidade_ComUnidadeCorrente_DevolveSoAsLinhasDela()
    {
        var c = await _banco.CriarCadastroAsync();
        var outra = await _banco.CriarCadastroAsync();
        var (ficha, _) = await _banco.EmitirAsync(c, 1);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = _banco.Contexto(scope, c.UnidadeId);
            (await db.Fichas.AsNoTracking().CountAsync(f => f.Id == ficha.Id)).ShouldBe(1);
            (await db.Estacoes.AsNoTracking().CountAsync()).ShouldBe(2);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = _banco.Contexto(scope, outra.UnidadeId);
            (await db.Fichas.AsNoTracking().CountAsync(f => f.Id == ficha.Id)).ShouldBe(0);
        }
    }

    [Fact]
    public async Task FiltroDeUnidade_ParametroDeEmpresaSempreVisivel_DeUnidadeSoNaPropria()
    {
        var c = await _banco.CriarCadastroAsync();
        var chave = $"teste.{Guid.NewGuid():N}";
        var empresa = Parametro.CriarDeEmpresa(chave, "empresa");
        var daUnidade = Parametro.CriarDeUnidade(c.UnidadeId, chave, "unidade");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = _banco.Contexto(scope, unidadeCorrente: null);
            db.Parametros.AddRange(empresa, daUnidade);
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = _banco.Contexto(scope, unidadeCorrente: null);
            var visiveis = await db.Parametros.AsNoTracking().Where(p => p.Chave == chave).Select(p => p.Id).ToListAsync();
            visiveis.ShouldBe([empresa.Id]);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = _banco.Contexto(scope, c.UnidadeId);
            var visiveis = await db.Parametros.AsNoTracking().Where(p => p.Chave == chave).Select(p => p.Id).ToListAsync();
            visiveis.ShouldBe([empresa.Id, daUnidade.Id], ignoreOrder: true);
        }
    }

    private static DateOnly DiaEmSaoPaulo(DateTimeOffset instante) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instante, TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")).DateTime);
}
