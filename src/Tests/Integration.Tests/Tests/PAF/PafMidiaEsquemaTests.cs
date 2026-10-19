using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using Integration.Tests.Infrastructure;
using Npgsql;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Media schema rules (M01) against real PostgreSQL: unique object keys, the panel order, the CHECKs and the
/// composite same-unit foreign keys. Assertions are on SqlState and constraint name, never on app checks;
/// the CHECK cases go around the domain factories with raw INSERTs, as an app bug or a script would.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafMidiaEsquemaTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private readonly PafBanco _banco = new(factory);

    public Task InitializeAsync() => _banco.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task EnvioMidia_DuasInsercoesConcorrentesDaMesmaChave_UmaFalhaNaRestricao()
    {
        var c = await _banco.CriarCadastroAsync();
        var chave = NovaChave(c.UnidadeId);
        var largada = new Largada(2);

        var resultados = await Task.WhenAll(
            _banco.TentarAsync(db => SalvarAsync(db, NovoEnvio(c.UnidadeId, chave)), largada),
            _banco.TentarAsync(db => SalvarAsync(db, NovoEnvio(c.UnidadeId, chave)), largada));

        resultados.Count(r => r is null).ShouldBe(1, "exatamente uma transação deve confirmar");
        AssertViolacao(resultados.Single(r => r is not null), PostgresErrorCodes.UniqueViolation, "UX_EnviosMidia_ChaveObjeto");
    }

    [Fact]
    public async Task Midia_DuasMidiasNoMesmoObjeto_FalhaNaRestricao()
    {
        var c = await _banco.CriarCadastroAsync();
        var envio = await EnviarAsync(c.UnidadeId);
        (await _banco.TentarAsync(db => SalvarAsync(db, NovaMidia(envio)))).ShouldBeNull();

        AssertViolacao(
            await _banco.TentarAsync(db => SalvarAsync(db, NovaMidia(envio))),
            PostgresErrorCodes.UniqueViolation,
            "UX_Midias_ChaveObjeto");
    }

    [Fact]
    public async Task Midia_SemEnvioOuComChaveDiferenteDaDoEnvio_FalhaNaChaveEstrangeira()
    {
        var c = await _banco.CriarCadastroAsync();
        var envio = await EnviarAsync(c.UnidadeId);

        AssertViolacao(
            await _banco.TentarAsync(db => InserirMidiaAsync(db, c.UnidadeId, Guid.NewGuid(), NovaChave(c.UnidadeId))),
            PostgresErrorCodes.ForeignKeyViolation,
            "FK_Midias_EnviosMidia_MesmaUnidade");
        AssertViolacao(
            await _banco.TentarAsync(db => InserirMidiaAsync(db, c.UnidadeId, envio.Id, NovaChave(c.UnidadeId))),
            PostgresErrorCodes.ForeignKeyViolation,
            "FK_Midias_EnviosMidia_MesmaUnidade");
        (await _banco.TentarAsync(db => InserirMidiaAsync(db, c.UnidadeId, envio.Id, envio.ChaveObjeto))).ShouldBeNull();
    }

    [Fact]
    public async Task PainelMidia_OrdemRepetidaNoMesmoPainel_Falha_EmOutroPainelPassa()
    {
        var c = await _banco.CriarCadastroAsync();
        var midia = await CriarMidiaAsync(c.UnidadeId);
        (await _banco.TentarAsync(db => SalvarAsync(db, PainelMidia.Criar(c.UnidadeId, c.GuicheA, midia.Id, ordem: 1)))).ShouldBeNull();

        // The same mídia may come back later in the list; only the position is unique.
        (await _banco.TentarAsync(db => SalvarAsync(db, PainelMidia.Criar(c.UnidadeId, c.GuicheA, midia.Id, ordem: 2)))).ShouldBeNull();
        AssertViolacao(
            await _banco.TentarAsync(db => SalvarAsync(db, PainelMidia.Criar(c.UnidadeId, c.GuicheA, midia.Id, ordem: 1))),
            PostgresErrorCodes.UniqueViolation,
            "UX_PainelMidias_Ordem");
        (await _banco.TentarAsync(db => SalvarAsync(db, PainelMidia.Criar(c.UnidadeId, c.GuicheB, midia.Id, ordem: 1)))).ShouldBeNull();
    }

    [Fact]
    public async Task ChavesCompostas_ReferenciaAOutraUnidade_FalhamNaChaveEstrangeiraDaMesmaUnidade()
    {
        var a = await _banco.CriarCadastroAsync();
        var b = await _banco.CriarCadastroAsync();
        var midiaB = await CriarMidiaAsync(b.UnidadeId);
        var envioB = await EnviarAsync(b.UnidadeId);

        // Row in A, mídia of B on a station of A.
        AssertViolacao(
            await _banco.TentarAsync(db => SalvarAsync(db, PainelMidia.Criar(a.UnidadeId, a.GuicheA, midiaB.Id, ordem: 1))),
            PostgresErrorCodes.ForeignKeyViolation,
            "FK_PainelMidias_Midias_MesmaUnidade");
        // Row in B, so the mídia matches, but the station is of A.
        AssertViolacao(
            await _banco.TentarAsync(db => SalvarAsync(db, PainelMidia.Criar(b.UnidadeId, a.GuicheA, midiaB.Id, ordem: 1))),
            PostgresErrorCodes.ForeignKeyViolation,
            "FK_PainelMidias_Estacoes_MesmaUnidade");
        AssertViolacao(
            await _banco.TentarAsync(db => SalvarAsync(db, NovaProgramacao(a.UnidadeId, midiaB.Id))),
            PostgresErrorCodes.ForeignKeyViolation,
            "FK_ProgramacoesMidia_Midias_MesmaUnidade");
        AssertViolacao(
            await _banco.TentarAsync(db => InserirMidiaAsync(db, a.UnidadeId, envioB.Id, envioB.ChaveObjeto)),
            PostgresErrorCodes.ForeignKeyViolation,
            "FK_Midias_EnviosMidia_MesmaUnidade");

        // Control: the same rows inside B are accepted.
        (await _banco.TentarAsync(db => SalvarAsync(db, PainelMidia.Criar(b.UnidadeId, b.GuicheA, midiaB.Id, ordem: 1)))).ShouldBeNull();
        (await _banco.TentarAsync(db => SalvarAsync(db, NovaProgramacao(b.UnidadeId, midiaB.Id)))).ShouldBeNull();
    }

    [Theory]
    [InlineData("Video", 0, "CK_Midias_Duracao")]
    [InlineData("Video", 86_401, "CK_Midias_Duracao")]
    [InlineData("Video", -5, "CK_Midias_Duracao")]
    [InlineData("Pdf", 10, "CK_Midias_Tipo")]
    public async Task Midias_ValorForaDaRegra_FalhaNoCheck(string tipo, int duracao, string restricao)
    {
        var c = await _banco.CriarCadastroAsync();
        var envio = await EnviarAsync(c.UnidadeId);

        var falha = await _banco.TentarAsync(db => db.Database.ExecuteSqlAsync($"""
            INSERT INTO paf."Midias" ("Id", "UnidadeId", "Descricao", "Tipo", "EnvioId", "ChaveObjeto", "DuracaoSegundos", "Ativa", "TenantId")
            VALUES ({Guid.NewGuid()}, {c.UnidadeId}, 'Vídeo', {tipo}, {envio.Id}, {envio.ChaveObjeto}, {duracao}, true, {TestConstants.RootTenantId})
            """));

        AssertViolacao(falha, PostgresErrorCodes.CheckViolation, restricao);
    }

    [Theory]
    [InlineData("Pdf", false, "CK_EnviosMidia_Tipo")]
    [InlineData("Video", true, "CK_EnviosMidia_Desfecho")]
    public async Task EnviosMidia_ValorForaDaRegra_FalhaNoCheck(string tipo, bool confirmadoERecusado, string restricao)
    {
        var c = await _banco.CriarCadastroAsync();
        DateTimeOffset? desfecho = confirmadoERecusado ? DateTimeOffset.UtcNow : null;

        var falha = await _banco.TentarAsync(db => db.Database.ExecuteSqlAsync($"""
            INSERT INTO paf."EnviosMidia" ("Id", "UnidadeId", "ChaveObjeto", "Tipo", "ContentType", "ConfirmadoEm", "RecusadoEm", "TenantId")
            VALUES ({Guid.NewGuid()}, {c.UnidadeId}, {NovaChave(c.UnidadeId)}, {tipo}, 'video/mp4', {desfecho}, {desfecho}, {TestConstants.RootTenantId})
            """));

        AssertViolacao(falha, PostgresErrorCodes.CheckViolation, restricao);
    }

    [Theory]
    [InlineData("2026-10-19", "2026-10-18", 1, 127, "CK_ProgramacoesMidia_Periodo")]
    [InlineData("2026-10-18", "2026-10-18", 0, 127, "CK_ProgramacoesMidia_Repeticoes")]
    [InlineData("2026-10-18", "2026-10-18", 1, 0, "CK_ProgramacoesMidia_Dias")]
    [InlineData("2026-10-18", "2026-10-18", 1, 128, "CK_ProgramacoesMidia_Dias")]
    public async Task ProgramacoesMidia_ValorForaDaRegra_FalhaNoCheck(
        string dataInicial, string dataFinal, int repeticoes, int dias, string restricao)
    {
        var c = await _banco.CriarCadastroAsync();
        var midia = await CriarMidiaAsync(c.UnidadeId);
        var inicial = DateOnly.Parse(dataInicial, System.Globalization.CultureInfo.InvariantCulture);
        var final = DateOnly.Parse(dataFinal, System.Globalization.CultureInfo.InvariantCulture);

        var falha = await _banco.TentarAsync(db => db.Database.ExecuteSqlAsync($"""
            INSERT INTO paf."ProgramacoesMidia"
                ("Id", "UnidadeId", "MidiaId", "Nome", "DataInicial", "DataFinal", "HoraInicial", "Repeticoes", "Dias", "Ativa", "TenantId")
            VALUES ({Guid.NewGuid()}, {c.UnidadeId}, {midia.Id}, 'Manhã', {inicial}, {final}, {new TimeOnly(9, 0)},
                    {repeticoes}, {dias}, true, {TestConstants.RootTenantId})
            """));

        AssertViolacao(falha, PostgresErrorCodes.CheckViolation, restricao);
    }

    [Fact]
    public async Task Limites_DentroDasRegras_SaoAceitos()
    {
        var c = await _banco.CriarCadastroAsync();
        var curta = await CriarMidiaAsync(c.UnidadeId, duracaoSegundos: 1);
        var longa = await CriarMidiaAsync(c.UnidadeId, duracaoSegundos: Midia.DuracaoMaximaSegundos);
        var dia = new DateOnly(2026, 10, 18);

        (await _banco.TentarAsync(db => SalvarAsync(db, ProgramacaoMidia.Criar(
            c.UnidadeId, curta.Id, "Um dia, um domingo", dia, dia, new TimeOnly(23, 59), repeticoes: 1, DiasSemana.Domingo)))).ShouldBeNull();
        (await _banco.TentarAsync(db => SalvarAsync(db, ProgramacaoMidia.Criar(
            c.UnidadeId, longa.Id, "Todos os dias", dia, dia.AddYears(1), new TimeOnly(0, 0), repeticoes: 1, DiasSemana.Todos)))).ShouldBeNull();
    }

    [Fact]
    public async Task FiltroDeUnidade_SemUnidadeCorrenteNadaAparece_ComAUnidadeSoAsLinhasDela()
    {
        var a = await _banco.CriarCadastroAsync();
        var b = await _banco.CriarCadastroAsync();
        var midia = await CriarMidiaAsync(a.UnidadeId);
        (await _banco.TentarAsync(db => SalvarAsync(db, PainelMidia.Criar(a.UnidadeId, a.GuicheA, midia.Id, ordem: 1)))).ShouldBeNull();
        (await _banco.TentarAsync(db => SalvarAsync(db, NovaProgramacao(a.UnidadeId, midia.Id)))).ShouldBeNull();

        (await ContarAsync(unidadeCorrente: null, a.UnidadeId)).ShouldBe(new Contagem(0, 0, 0, 0));
        (await ContarAsync(b.UnidadeId, a.UnidadeId)).ShouldBe(new Contagem(0, 0, 0, 0));
        (await ContarAsync(a.UnidadeId, a.UnidadeId)).ShouldBe(new Contagem(1, 1, 1, 1));

        using var scope = factory.Services.CreateScope();
        var db = _banco.Contexto(scope, unidadeCorrente: null);
        (await db.Midias.AsNoTracking().IgnoreQueryFilters([PafQueryFilters.Unidade]).CountAsync(m => m.Id == midia.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task DoisTenants_MesmaChaveEmCadaUm_AceitaENaoSeEnxergam()
    {
        var a = await _banco.CriarCadastroAsync();
        var outro = new PafTenantCliente(factory, "paf-midia-b");
        await outro.InicializarAsync();
        var unidadeOutro = await outro.CriarUnidadeAsync(limiteSessoes: null);
        var chave = NovaChave(a.UnidadeId);
        await EnviarAsync(a.UnidadeId, chave);

        // Finbuckle appends TenantId to the unique index: the same key in another tenant does not collide.
        using (var scope = factory.Services.CreateScope())
        {
            var db = outro.Contexto(scope, unidadeOutro.UnidadeId);
            db.EnviosMidia.Add(NovoEnvio(unidadeOutro.UnidadeId, chave));
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = _banco.Contexto(scope, unidadeCorrente: null);
            (await db.EnviosMidia.AsNoTracking().IgnoreQueryFilters([PafQueryFilters.Unidade])
                .CountAsync(e => e.ChaveObjeto == chave)).ShouldBe(1);
            (await db.EnviosMidia.AsNoTracking().IgnoreQueryFilters([PafQueryFilters.Unidade])
                .CountAsync(e => e.UnidadeId == unidadeOutro.UnidadeId)).ShouldBe(0);
        }
    }

    private sealed record Contagem(int Envios, int Midias, int Programacoes, int Painel);

    private async Task<Contagem> ContarAsync(Guid? unidadeCorrente, Guid unidadeDosDados)
    {
        using var scope = factory.Services.CreateScope();
        var db = _banco.Contexto(scope, unidadeCorrente);
        return new Contagem(
            await db.EnviosMidia.AsNoTracking().CountAsync(e => e.UnidadeId == unidadeDosDados),
            await db.Midias.AsNoTracking().CountAsync(m => m.UnidadeId == unidadeDosDados),
            await db.ProgramacoesMidia.AsNoTracking().CountAsync(p => p.UnidadeId == unidadeDosDados),
            await db.PainelMidias.AsNoTracking().CountAsync(p => p.UnidadeId == unidadeDosDados));
    }

    private static string NovaChave(Guid unidadeId) => $"paf-midia/teste/{unidadeId:N}/{Guid.CreateVersion7():N}.mp4";

    private static EnvioMidia NovoEnvio(Guid unidadeId, string chave) =>
        EnvioMidia.Criar(Guid.CreateVersion7(), unidadeId, chave, TipoMidia.Video, "video/mp4");

    private static Midia NovaMidia(EnvioMidia envio) => Midia.Criar(envio, "Vídeo institucional", duracaoSegundos: 30);

    // Goes around Midia.Criar, which takes unidade and key from the envio and cannot build these rows.
    private static Task<int> InserirMidiaAsync(PafDbContext db, Guid unidadeId, Guid envioId, string chave) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO paf."Midias" ("Id", "UnidadeId", "Descricao", "Tipo", "EnvioId", "ChaveObjeto", "DuracaoSegundos", "Ativa", "TenantId")
            VALUES ({Guid.NewGuid()}, {unidadeId}, 'Vídeo', 'Video', {envioId}, {chave}, 30, true, {TestConstants.RootTenantId})
            """);

    private static ProgramacaoMidia NovaProgramacao(Guid unidadeId, Guid midiaId) =>
        ProgramacaoMidia.Criar(unidadeId, midiaId, "Manhã", new DateOnly(2026, 10, 18), new DateOnly(2026, 12, 31),
            new TimeOnly(9, 0), repeticoes: 2, DiasSemana.Segunda | DiasSemana.Sexta);

    private Task<EnvioMidia> EnviarAsync(Guid unidadeId) => EnviarAsync(unidadeId, NovaChave(unidadeId));

    private async Task<EnvioMidia> EnviarAsync(Guid unidadeId, string chave)
    {
        var envio = NovoEnvio(unidadeId, chave);
        (await _banco.TentarAsync(db => SalvarAsync(db, envio))).ShouldBeNull();
        return envio;
    }

    private Task<Midia> CriarMidiaAsync(Guid unidadeId) => CriarMidiaAsync(unidadeId, duracaoSegundos: 30);

    private async Task<Midia> CriarMidiaAsync(Guid unidadeId, int duracaoSegundos)
    {
        var envio = await EnviarAsync(unidadeId);
        var midia = Midia.Criar(envio, "Vídeo institucional", duracaoSegundos);
        (await _banco.TentarAsync(db => SalvarAsync(db, midia))).ShouldBeNull();
        return midia;
    }

    private static async Task SalvarAsync<T>(PafDbContext db, T entidade)
        where T : class
    {
        db.Add(entidade);
        await db.SaveChangesAsync();
    }

    private static void AssertViolacao(PostgresException? falha, string sqlState, string restricao)
    {
        falha.ShouldNotBeNull($"esperava violação de {restricao}");
        falha.SqlState.ShouldBe(sqlState);
        falha.ConstraintName.ShouldBe(restricao);
    }
}
