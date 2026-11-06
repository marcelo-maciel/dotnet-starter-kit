using FSH.Modules.PAF.Domain;
using Integration.Tests.Infrastructure;
using Npgsql;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// The database rules of seção 4.2 / ADR 0005, exercised against real PostgreSQL. Concurrent cases run
/// two transactions on two connections that write together, so the loser blocks on the index entry and
/// fails when the winner commits; the assertion is on SqlState and constraint name, never on app checks.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafRestricoesTests : IAsyncLifetime
{
    private const string UniqueViolation = PostgresErrorCodes.UniqueViolation;
    private readonly PafBanco _banco;

    public PafRestricoesTests(FshWebApplicationFactory factory) => _banco = new PafBanco(factory);

    public Task InitializeAsync() => _banco.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Numeracao_DuasEmissoesConcorrentesComOMesmoNumero_SoUmaConfirma()
    {
        var c = await _banco.CriarCadastroAsync();
        var largada = new Largada(2);

        var resultados = await Task.WhenAll(
            _banco.TentarAsync(db => SalvarAsync(db, PafBanco.NovaFicha(c, 7, c.GuicheA, "totem-1")), largada),
            _banco.TentarAsync(db => SalvarAsync(db, PafBanco.NovaFicha(c, 7, c.GuicheB, "totem-2")), largada));

        UnicaFalha(resultados, "UX_Fichas_Numeracao");
    }

    [Fact]
    public async Task Numeracao_NumerosDiferentesNaMesmaCombinacao_NaoColidem()
    {
        var c = await _banco.CriarCadastroAsync();

        (await _banco.TentarAsync(db => SalvarAsync(db, PafBanco.NovaFicha(c, 1, c.GuicheA, "k1")))).ShouldBeNull();
        (await _banco.TentarAsync(db => SalvarAsync(db, PafBanco.NovaFicha(c, 2, c.GuicheA, "k2")))).ShouldBeNull();
    }

    [Fact]
    public async Task ChaveIdempotencia_ReenvioDaMesmaEstacao_FalhaNaRestricao()
    {
        var c = await _banco.CriarCadastroAsync();
        (await _banco.TentarAsync(db => SalvarAsync(db, PafBanco.NovaFicha(c, 1, c.GuicheA, "ciclo-1")))).ShouldBeNull();

        // The resend carries a recalculated number; only the key collides.
        var reenvio = await _banco.TentarAsync(db => SalvarAsync(db, PafBanco.NovaFicha(c, 2, c.GuicheA, "ciclo-1")));
        AssertViolacao(reenvio, "UX_Fichas_ChaveIdempotencia");

        // The key is per station: another totem may reuse the same value.
        (await _banco.TentarAsync(db => SalvarAsync(db, PafBanco.NovaFicha(c, 3, c.GuicheB, "ciclo-1")))).ShouldBeNull();
    }

    [Fact]
    public async Task FichaEmCurso_DoisGuichesChamamAtendimentosDaMesmaFicha_SoUmConfirma()
    {
        var c = await _banco.CriarCadastroAsync();
        var (ficha, primeiro) = await _banco.EmitirAsync(c, 1);
        // Forwarding is the only path that gives a ficha two waiting atendimentos.
        var segundo = await _banco.NovoAtendimentoAsync(c, ficha.Id);
        var largada = new Largada(2);

        var resultados = await Task.WhenAll(
            _banco.TentarAsync(db => PafBanco.ChamarAsync(db, primeiro.Id, c.GuicheA), largada),
            _banco.TentarAsync(db => PafBanco.ChamarAsync(db, segundo.Id, c.GuicheB), largada));

        UnicaFalha(resultados, "UX_Atendimentos_FichaEmCurso");
    }

    [Fact]
    public async Task FichaEmCurso_DepoisDeEncerrado_OutroAtendimentoDaFichaPodeSerChamado()
    {
        var c = await _banco.CriarCadastroAsync();
        var (ficha, primeiro) = await _banco.EmitirAsync(c, 1);
        var segundo = await _banco.NovoAtendimentoAsync(c, ficha.Id);

        (await _banco.TentarAsync(db => PafBanco.ChamarAsync(db, primeiro.Id, c.GuicheA))).ShouldBeNull();
        AssertViolacao(await _banco.TentarAsync(db => PafBanco.ChamarAsync(db, segundo.Id, c.GuicheB)), "UX_Atendimentos_FichaEmCurso");

        (await _banco.TentarAsync(db => PafBanco.EncerrarAsync(db, primeiro.Id))).ShouldBeNull();
        (await _banco.TentarAsync(db => PafBanco.ChamarAsync(db, segundo.Id, c.GuicheB))).ShouldBeNull();
    }

    [Fact]
    public async Task EstacaoEmCurso_CliqueDuploChamaDuasFichasNoMesmoGuiche_SoUmConfirma()
    {
        var c = await _banco.CriarCadastroAsync();
        var (_, atendimento1) = await _banco.EmitirAsync(c, 1);
        var (_, atendimento2) = await _banco.EmitirAsync(c, 2);
        var largada = new Largada(2);

        var resultados = await Task.WhenAll(
            _banco.TentarAsync(db => PafBanco.ChamarAsync(db, atendimento1.Id, c.GuicheA), largada),
            _banco.TentarAsync(db => PafBanco.ChamarAsync(db, atendimento2.Id, c.GuicheA), largada));

        UnicaFalha(resultados, "UX_Atendimentos_EstacaoEmCurso");
    }

    [Fact]
    public async Task LetraUnicaPorUnidade_OutraFilaComAMesmaLetra_FalhaNaRestricao()
    {
        var c = await _banco.CriarCadastroAsync();
        var outra = await _banco.CriarCadastroAsync();

        var repetida = await _banco.TentarAsync(db =>
        {
            var fila = Fila.Criar(c.UnidadeId, "Protocolo", "Protocolo", setorId: null);
            db.Filas.Add(fila);
            db.FilaPrioridades.Add(FilaPrioridade.Criar(c.UnidadeId, fila.Id, c.PrioridadeId, "A", numeroInicial: 1, ordem: 1));
            return db.SaveChangesAsync();
        });
        AssertViolacao(repetida, "UX_FilaPrioridades_Letra");

        // Another unidade of the same tenant may use the same letter.
        var outraUnidade = await _banco.TentarAsync(db =>
        {
            var fila = Fila.Criar(outra.UnidadeId, "Protocolo", "Protocolo", setorId: null);
            db.Filas.Add(fila);
            db.FilaPrioridades.Add(FilaPrioridade.Criar(outra.UnidadeId, fila.Id, outra.PrioridadeId, "B", numeroInicial: 1, ordem: 2));
            return db.SaveChangesAsync();
        });
        outraUnidade.ShouldBeNull();
    }

    [Fact]
    public async Task SessaoEstacao_GuicheOcupado_SessaoComDoisGuiches_ConcessaoComDoisGuiches_FalhamNaRestricao()
    {
        var c = await _banco.CriarCadastroAsync();
        var sessao = Guid.NewGuid();
        var concessao = Guid.NewGuid();
        var expira = DateTimeOffset.UtcNow.AddHours(1);

        (await _banco.TentarAsync(db => SalvarAsync(db, SessaoEstacao.CriarParaSessao(c.GuicheA, sessao)))).ShouldBeNull();

        AssertViolacao(
            await _banco.TentarAsync(db => SalvarAsync(db, SessaoEstacao.CriarParaSessao(c.GuicheA, Guid.NewGuid()))),
            "UX_SessoesEstacao_EstacaoAtiva");
        // Support selecting the guichê held by the real user fails on the same station index.
        AssertViolacao(
            await _banco.TentarAsync(db => SalvarAsync(db, SessaoEstacao.CriarParaConcessao(c.GuicheA, concessao, expira))),
            "UX_SessoesEstacao_EstacaoAtiva");
        AssertViolacao(
            await _banco.TentarAsync(db => SalvarAsync(db, SessaoEstacao.CriarParaSessao(c.GuicheB, sessao))),
            "UX_SessoesEstacao_SessaoAtiva");

        var outra = await _banco.CriarCadastroAsync();
        (await _banco.TentarAsync(db => SalvarAsync(db, SessaoEstacao.CriarParaConcessao(outra.GuicheA, concessao, expira)))).ShouldBeNull();
        AssertViolacao(
            await _banco.TentarAsync(db => SalvarAsync(db, SessaoEstacao.CriarParaConcessao(outra.GuicheB, concessao, expira))),
            "UX_SessoesEstacao_ConcessaoAtiva");
    }

    [Fact]
    public async Task SessaoEstacao_DepoisDaLiberacao_GuicheESessaoFicamLivres()
    {
        var c = await _banco.CriarCadastroAsync();
        var sessao = Guid.NewGuid();
        (await _banco.TentarAsync(db => SalvarAsync(db, SessaoEstacao.CriarParaSessao(c.GuicheA, sessao)))).ShouldBeNull();

        (await _banco.TentarAsync(db => db.Database.ExecuteSqlAsync($"""
            UPDATE paf."SessoesEstacao" SET "LiberadoEm" = now() WHERE "EstacaoId" = {c.GuicheA}
            """))).ShouldBeNull();

        (await _banco.TentarAsync(db => SalvarAsync(db, SessaoEstacao.CriarParaSessao(c.GuicheA, Guid.NewGuid())))).ShouldBeNull();
        (await _banco.TentarAsync(db => SalvarAsync(db, SessaoEstacao.CriarParaSessao(c.GuicheB, sessao)))).ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessaoEstacao_SemDonoOuComDoisDonos_FalhaNoCheck(bool doisDonos)
    {
        var c = await _banco.CriarCadastroAsync();
        Guid? sessao = doisDonos ? Guid.NewGuid() : null;
        Guid? concessao = doisDonos ? Guid.NewGuid() : null;

        var falha = await _banco.TentarAsync(db => db.Database.ExecuteSqlAsync($"""
            INSERT INTO paf."SessoesEstacao" ("Id", "EstacaoId", "SessaoId", "ConcessaoId", "TenantId")
            VALUES ({Guid.NewGuid()}, {c.GuicheA}, {sessao}, {concessao}, {TestConstants.RootTenantId})
            """));

        falha.ShouldNotBeNull();
        falha.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        falha.ConstraintName.ShouldBe("CK_SessoesEstacao_DonoUnico");
    }

    private static async Task SalvarAsync<T>(FSH.Modules.PAF.Data.PafDbContext db, T entidade)
        where T : class
    {
        db.Add(entidade);
        await db.SaveChangesAsync();
    }

    private static void AssertViolacao(PostgresException? falha, string restricao)
    {
        falha.ShouldNotBeNull($"esperava violação de {restricao}");
        falha.SqlState.ShouldBe(UniqueViolation);
        falha.ConstraintName.ShouldBe(restricao);
    }

    private static void UnicaFalha(PostgresException?[] resultados, string restricao)
    {
        resultados.Count(r => r is null).ShouldBe(1, "exatamente uma transação deve confirmar");
        AssertViolacao(resultados.Single(r => r is not null), restricao);
    }
}
