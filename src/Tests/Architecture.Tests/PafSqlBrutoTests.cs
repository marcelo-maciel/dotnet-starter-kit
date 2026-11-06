using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Raw SQL skips the global query filters, tenant included (CONTEXT.md, ADR 0003). Every PAF statement that
/// reads or writes a paf table must therefore name "TenantId" once per table it reads from or writes to
/// (FROM, UPDATE, INTO; a JOIN rides on an id taken from a tenant-filtered row). Counting instead of "the
/// statement mentions TenantId somewhere" is what catches a subquery without it, like the NOT EXISTS of the
/// call before slice 2. The scan reads ldstr operands of the compiled IL: an interpolated raw string passed
/// to SqlQuery/ExecuteSql compiles to one literal format string. String-built SQL would evade that, so the
/// *Raw APIs are banned in the module.
/// </summary>
public sealed partial class PafSqlBrutoTests
{
    private const string PafAssemblyFile = "FSH.Modules.PAF.dll";
    private const string MarcaTabelaPaf = "paf.\"";
    private const string ColunaTenant = "\"TenantId\"";

    // The first slice already has two dozen statements; far fewer means the scan stopped seeing them.
    private const int MinimoDeStatementsEsperado = 15;

    [Fact]
    public void Paf_RawStatements_Should_Carry_TenantId_Per_Table_Accessed()
    {
        using var modulo = ModuleDefinition.ReadModule(CaminhoDoAssembly(PafAssemblyFile));
        var statements = Statements(TodosOsTipos(modulo));

        statements.Count.ShouldBeGreaterThanOrEqualTo(MinimoDeStatementsEsperado,
            $"the scan found only {statements.Count} raw statements on paf tables; it is not reading the IL");
        var violacoes = statements.Where(s => SemTenantSuficiente(s.Sql)).Select(s => $"{s.Metodo}: {s.Sql}").ToList();
        violacoes.ShouldBeEmpty(
            $"Raw SQL skips the tenant filter; add \"TenantId\" for every table read or written:{Environment.NewLine}" +
            string.Join(Environment.NewLine, violacoes));
    }

    [Fact]
    public void Paf_Should_Not_Call_Raw_Sql_Apis()
    {
        using var modulo = ModuleDefinition.ReadModule(CaminhoDoAssembly(PafAssemblyFile));
        var chamadas = ChamadasRaw(TodosOsTipos(modulo));
        chamadas.ShouldBeEmpty(
            $"Use the interpolated SqlQuery/ExecuteSql so the statement is a literal the barrier can read. Found: {string.Join(", ", chamadas)}");
    }

    [Fact]
    public void Barreira_ReprovaStatementSinteticoSemTenant_EAprovaOConforme()
    {
        using var modulo = ModuleDefinition.ReadModule(typeof(PafSqlBrutoTests).Assembly.Location);
        var tipos = TodosOsTipos(modulo).ToList();

        var semTenant = Statements(tipos.Where(t => Pertence(t, nameof(ConsultaSinteticaSemTenant))));
        semTenant.Count.ShouldBe(2);
        semTenant.ShouldAllBe(s => SemTenantSuficiente(s.Sql));

        var conforme = Statements(tipos.Where(t => Pertence(t, nameof(ConsultaSinteticaConforme))));
        conforme.Count.ShouldBe(1);
        conforme.ShouldAllBe(s => !SemTenantSuficiente(s.Sql));

        ChamadasRaw(tipos.Where(t => Pertence(t, nameof(ConsultaSinteticaSemTenant)))).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("""SELECT 1 FROM paf."Atendimentos" WHERE "TenantId" = {0}""", false)]
    [InlineData("""UPDATE paf."Filas" SET "UltimaChamadaEm" = now() WHERE "Id" = {0}""", true)]
    [InlineData("""DELETE FROM paf."AtividadesSessao" WHERE "SessaoId" = {0}""", true)]
    [InlineData("""INSERT INTO paf."AtividadesSessao" ("SessaoId", "TenantId") VALUES ({0}, {1})""", false)]
    [InlineData("""SELECT 1 FROM paf."Atendimentos" a JOIN paf."Fichas" f ON f."Id" = a."FichaId" WHERE a."TenantId" = {0}""", false)]
    [InlineData("""SELECT 1 FROM paf."Atendimentos" a WHERE a."TenantId" = {0} AND NOT EXISTS (SELECT 1 FROM paf."Atendimentos" o WHERE o."FichaId" = a."FichaId")""", true)]
    [InlineData("""select 1 from paf."Atendimentos" where "tenantid" = {0}""", true)]
    public void Regra_ContaTenantPorTabelaAcessada(string sql, bool reprova) =>
        SemTenantSuficiente(sql).ShouldBe(reprova);

    private static bool SemTenantSuficiente(string sql) =>
        Ocorrencias(sql, ColunaTenant) < AcessoATabela().Count(sql);

    private static int Ocorrencias(string texto, string trecho) =>
        (texto.Length - texto.Replace(trecho, string.Empty, StringComparison.Ordinal).Length) / trecho.Length;

    [GeneratedRegex("""\b(FROM|UPDATE|INTO)\s+paf\.""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcessoATabela();

    private static List<(string Metodo, string Sql)> Statements(IEnumerable<TypeDefinition> tipos) =>
        tipos.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Ldstr && i.Operand is string s && s.Contains(MarcaTabelaPaf, StringComparison.Ordinal))
                .Select(i => (m.FullName, (string)i.Operand)))
            .ToList();

    private static List<string> ChamadasRaw(IEnumerable<TypeDefinition> tipos) =>
        tipos.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions
                .Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                    && i.Operand is MethodReference alvo
                    && alvo.DeclaringType.Namespace.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                    && (alvo.Name.EndsWith("Raw", StringComparison.Ordinal) || alvo.Name.EndsWith("RawAsync", StringComparison.Ordinal)))
                .Select(i => $"{m.FullName} -> {((MethodReference)i.Operand).Name}"))
            .ToList();

    private static bool Pertence(TypeDefinition tipo, string nome)
    {
        for (var atual = tipo; atual is not null; atual = atual.DeclaringType)
        {
            if (atual.Name == nome)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<TypeDefinition> TodosOsTipos(ModuleDefinition modulo)
    {
        var pendentes = new Stack<TypeDefinition>(modulo.Types);
        while (pendentes.Count > 0)
        {
            var tipo = pendentes.Pop();
            yield return tipo;
            foreach (var aninhado in tipo.NestedTypes)
            {
                pendentes.Push(aninhado);
            }
        }
    }

    private static string CaminhoDoAssembly(string arquivo)
    {
        string caminho = Path.Combine(AppContext.BaseDirectory, arquivo);
        File.Exists(caminho).ShouldBeTrue($"{arquivo} not found next to the tests; the scan would pass on nothing");
        return caminho;
    }
}

// Synthetic statements compiled into this assembly: the barrier must reject these from the IL, not only from strings.
internal static class ConsultaSinteticaSemTenant
{
    public static FormattableString SubconsultaSemTenant(string tenantId, Guid fichaId) => $"""
        SELECT 1 FROM paf."Atendimentos" a
         WHERE a."TenantId" = {tenantId} AND a."FichaId" = {fichaId}
           AND NOT EXISTS (SELECT 1 FROM paf."Atendimentos" o WHERE o."FichaId" = a."FichaId")
        """;

    public static FormattableString UpdateSemTenant(Guid filaId) =>
        $"""UPDATE paf."Filas" SET "UltimaChamadaEm" = now() WHERE "Id" = {filaId}""";

    public static Task<int> MontadoEmTextoAsync(DbContext db, string sqlMontado) =>
        db.Database.ExecuteSqlRawAsync(sqlMontado);
}

internal static class ConsultaSinteticaConforme
{
    public static FormattableString Conforme(string tenantId, Guid fichaId) => $"""
        SELECT 1 FROM paf."Atendimentos" a
         WHERE a."TenantId" = {tenantId} AND a."FichaId" = {fichaId}
           AND NOT EXISTS (SELECT 1 FROM paf."Atendimentos" o WHERE o."TenantId" = {tenantId} AND o."FichaId" = a."FichaId")
        """;
}
