using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.CompilerServices;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Atendimentos.ChamarProximaFicha;
using Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// The candidate query of the call (the hottest path, requisitos seção 4) is served by IX_Atendimentos_Candidatos
/// on a unidade with history. EXPLAIN runs on the handler's own statement text, not on a copy.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafIndiceCandidatosTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Indice = "IX_Atendimentos_Candidatos";
    private const string IndicePrioridade = "IX_Atendimentos_PrioridadeId";
    private const int Historico = 100_000;
    private const int Amostras = 8;
    private const int RuidoPrioridades = 250;
    private const int RuidoPorPrioridade = 8;

    // One waiting atendimento per thousand: the live queue of a busy unidade against a year of closed ones.
    private const int UmAguardandoACada = 1_000;

    private static readonly TimeSpan PrazoCarga = TimeSpan.FromMinutes(3);

    private readonly PafTenantCliente _tenant = new(factory, "paf-idx");
    private PafOperacao _op = default!;

    public async ValueTask InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public void Modelo_DeclaraOIndiceParcialDosCandidatos()
    {
        using var scope = factory.Services.CreateScope();
        var indice = IndiceDoModelo(_tenant.Contexto(scope, unidadeCorrente: null));

        indice.Properties.Select(p => p.Name).ShouldBe([nameof(Atendimento.UnidadeId), nameof(Atendimento.FilaId), nameof(Atendimento.DataElegibilidade)]);
        indice.GetFilter().ShouldBe("\"Estado\" = 'Aguardando'");
        indice.IsUnique.ShouldBeFalse();
    }

    [Fact]
    public async Task Explain_ComCemMilAtendimentos_ConsultaDeCandidatosUsaOIndice()
    {
        var c = await _op.CriarAsync();
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        db.Database.SetCommandTimeout(PrazoCarga);

        // Until the wave migration (C01) creates it, the index is built here from the model; a no-op afterwards.
        await db.Database.ExecuteSqlRawAsync(CriarIndiceDoModelo(IndiceDoModelo(db)));

        // The load lives in a transaction that is rolled back: deleting it row by row instead re-checks
        // FK_Atendimentos_Fichas_FichaId per ficha with no plain index on Atendimentos.FichaId, which takes minutes.
        try
        {
            await using var carga = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlAsync($"""
                WITH f AS (
                    INSERT INTO paf."Fichas" ("Id", "UnidadeId", "FilaId", "PrioridadeId", "Letra", "Numero", "IdentificadorPublico",
                                              "Canal", "EstacaoId", "ChaveIdempotencia", "CriadoEm", "TenantId")
                    SELECT gen_random_uuid(), {c.UnidadeId}, {c.Caixa}, {c.Normal}, 'A', i, gen_random_uuid(),
                           'Totem', {c.Totem}, 'carga-' || i, now() - make_interval(days => i % 365, secs => i % 600 + 10), {_tenant.TenantId}
                      FROM generate_series(1, {Historico}) AS i
                    RETURNING "Id", "Numero", "CriadoEm")
                INSERT INTO paf."Atendimentos" ("Id", "UnidadeId", "FichaId", "FilaId", "PrioridadeId", "Estado", "CriadoEm", "ElegivelDesde", "TenantId")
                SELECT gen_random_uuid(), {c.UnidadeId}, f."Id", {c.Caixa}, {c.Normal},
                       CASE WHEN f."Numero" % {UmAguardandoACada} = 0 THEN 'Aguardando' ELSE 'Encerrado' END,
                       f."CriadoEm", f."CriadoEm", {_tenant.TenantId}
                  FROM f
                """);
            // What the rest of the suite leaves in the table when this test runs in it (CI, 09/11/2026: about 2 000
            // atendimentos over 253 other prioridades, every one with an id below this unidade's Normal). Without it the
            // plan this test guards against never shows up when the class runs alone.
            await db.Database.ExecuteSqlAsync($"""
                WITH pr AS (
                    INSERT INTO paf."Prioridades" ("Id", "UnidadeId", "Nome", "Valor", "Ativa", "TenantId")
                    SELECT uuidv7(make_interval(mins => i - {RuidoPrioridades} - 60)), {c.UnidadeId}, 'ruido-' || i, 0, true, {_tenant.TenantId}
                      FROM generate_series(1, {RuidoPrioridades}) AS i
                    RETURNING "Id"),
                f AS (
                    INSERT INTO paf."Fichas" ("Id", "UnidadeId", "FilaId", "PrioridadeId", "Letra", "Numero", "IdentificadorPublico",
                                              "Canal", "EstacaoId", "ChaveIdempotencia", "CriadoEm", "TenantId")
                    SELECT gen_random_uuid(), {c.UnidadeId}, {c.Caixa}, pr."Id", 'Z', row_number() OVER (), gen_random_uuid(),
                           'Totem', {c.Totem}, 'ruido-' || pr."Id" || '-' || k, now() - interval '400 days', {_tenant.TenantId}
                      FROM pr CROSS JOIN generate_series(1, {RuidoPorPrioridade}) AS k
                    RETURNING "Id", "PrioridadeId", "CriadoEm")
                INSERT INTO paf."Atendimentos" ("Id", "UnidadeId", "FichaId", "FilaId", "PrioridadeId", "Estado", "CriadoEm", "ElegivelDesde", "TenantId")
                SELECT gen_random_uuid(), {c.UnidadeId}, f."Id", {c.Caixa}, f."PrioridadeId", 'Encerrado', f."CriadoEm", f."CriadoEm", {_tenant.TenantId}
                  FROM f
                """);

            // ANALYZE samples 30 000 of the rows at random, and each sample moves the costs a little. Before the
            // prioridade join went LATERAL, a merge join walking IX_Atendimentos_PrioridadeId came within 3% of the
            // index plan and won on some samples (PostgreSQL 18, 09/11/2026): it reads every row of the unidade's
            // dominant prioridade. Several samples, both values of RF06 "somente fichas da data atual".
            for (var amostra = 1; amostra <= Amostras; amostra++)
            {
                await db.Database.ExecuteSqlRawAsync("""ANALYZE paf."Fichas", paf."Atendimentos" """);
                foreach (var somenteDataAtual in new[] { true, false })
                {
                    var plano = await ExplainAsync(db, ChamarProximaFichaCommandHandler.ConsultaCandidato(
                        _tenant.TenantId, c.UnidadeId, c.GuicheA, respeitaOrdem: false, somenteDataAtual, []));
                    var contexto = $"amostra {amostra}, somenteDataAtual={somenteDataAtual}, plano:{Environment.NewLine}{plano}";
                    plano.ShouldContain(Indice, Case.Sensitive, contexto);
                    plano.ShouldNotContain(IndicePrioridade, Case.Sensitive, contexto);
                }
            }
        }
        finally
        {
            // The rollback leaves 100 000 dead tuples that later tests' scans would walk; reclaim them and reset the statistics.
            await db.Database.ExecuteSqlRawAsync("""VACUUM ANALYZE paf."Fichas", paf."Atendimentos" """);
        }
    }

    private static IIndex IndiceDoModelo(DbContext db) =>
        db.Model.FindEntityType(typeof(Atendimento))!.GetIndexes().Single(i => i.GetDatabaseName() == Indice);

    private static string CriarIndiceDoModelo(IIndex indice)
    {
        var tabela = StoreObjectIdentifier.Table(indice.DeclaringEntityType.GetTableName()!, indice.DeclaringEntityType.GetSchema());
        var colunas = string.Join(", ", indice.Properties.Select(p => $"\"{p.GetColumnName(tabela)}\""));
        return $"""CREATE INDEX IF NOT EXISTS "{indice.GetDatabaseName()}" ON {tabela.Schema}."{tabela.Name}" ({colunas}) WHERE {indice.GetFilter()}""";
    }

    /// <summary>EXPLAIN of an EF FormattableString, through EF so the values bind exactly as the handler binds them.</summary>
    private static async Task<string> ExplainAsync(DbContext db, FormattableString consulta)
    {
        var explain = FormattableStringFactory.Create("EXPLAIN " + consulta.Format, consulta.GetArguments());
        var linhas = await db.Database.SqlQuery<LinhaDoPlano>(explain).ToListAsync();
        return string.Join(Environment.NewLine, linhas.Select(l => l.Linha));
    }

    private sealed record LinhaDoPlano([property: Column("QUERY PLAN")] string Linha);
}
