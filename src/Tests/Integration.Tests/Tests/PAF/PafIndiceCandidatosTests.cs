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
    private const int Historico = 100_000;

    // One waiting atendimento per thousand: the live queue of a busy unidade against a year of closed ones.
    private const int UmAguardandoACada = 1_000;

    private static readonly TimeSpan PrazoCarga = TimeSpan.FromMinutes(3);

    private readonly PafTenantCliente _tenant = new(factory, "paf-idx");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => Task.CompletedTask;

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
            await db.Database.ExecuteSqlRawAsync("""ANALYZE paf."Fichas", paf."Atendimentos" """);

            // Parameter on (the default; requisitos seção 4: no installed client turns it off). With it off the
            // planner may pick a merge join on IX_Atendimentos_PrioridadeId depending on the id order, so that
            // mode is not asserted here.
            var plano = await ExplainAsync(db, ChamarProximaFichaCommandHandler.ConsultaCandidato(
                _tenant.TenantId, c.UnidadeId, c.GuicheA, respeitaOrdem: false, somenteDataAtual: true, []));
            plano.ShouldContain(Indice, Case.Sensitive, $"plano:{Environment.NewLine}{plano}");
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
