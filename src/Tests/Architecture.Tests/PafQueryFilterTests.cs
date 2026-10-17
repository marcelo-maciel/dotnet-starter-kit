using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// ADR 0003 barrier: inside the PAF module, the parameterless IgnoreQueryFilters() would drop the tenant
/// filter together with the unit filter and turn a consolidated read into a read of the whole database.
/// The scan reads the compiled IL, so lambdas, async state machines and local functions are covered too.
/// </summary>
public sealed class PafQueryFilterTests
{
    private const string PafAssemblyFile = "FSH.Modules.PAF.dll";
    private const string EfQueryableExtensions = "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions";

    [Fact]
    public void Paf_Should_Not_Call_Parameterless_IgnoreQueryFilters()
    {
        var chamadas = ChamadasDeIgnoreQueryFilters();

        // Positive control: the aggregation path calls the named overload, so a scan that sees nothing is broken.
        chamadas.ShouldContain(c => c.Parametros == 2,
            "the scan found no IgnoreQueryFilters([name]) call; the PAF aggregation path should have one");

        var proibidas = chamadas.Where(c => c.Parametros == 1).Select(c => c.Metodo).ToList();
        proibidas.ShouldBeEmpty(
            "PAF must turn off only the unit filter, by name: IgnoreQueryFilters([PafQueryFilters.Unidade]). " +
            $"Parameterless calls found in: {string.Join(", ", proibidas)}");
    }

    [Fact]
    public void Paf_Should_Not_Declare_A_Specification_That_Ignores_Query_Filters()
    {
        // The kit's SpecificationEvaluator turns ISpecification.IgnoreQueryFilters into the parameterless
        // call, so any PAF type that implements or overrides that getter is the same hazard.
        using var modulo = ModuleDefinition.ReadModule(CaminhoDoAssembly());
        var declaracoes = TodosOsTipos(modulo)
            .SelectMany(t => t.Methods)
            .Where(m => m.Name.EndsWith("get_IgnoreQueryFilters", StringComparison.Ordinal))
            .Select(m => m.FullName)
            .ToList();

        declaracoes.ShouldBeEmpty(
            $"PAF specifications must not turn IgnoreQueryFilters on. Found: {string.Join(", ", declaracoes)}");
    }

    private static List<(string Metodo, int Parametros)> ChamadasDeIgnoreQueryFilters()
    {
        using var modulo = ModuleDefinition.ReadModule(CaminhoDoAssembly());
        var chamadas = new List<(string, int)>();
        foreach (var metodo in TodosOsTipos(modulo).SelectMany(t => t.Methods).Where(m => m.HasBody))
        {
            foreach (var instrucao in metodo.Body.Instructions)
            {
                if ((instrucao.OpCode == OpCodes.Call || instrucao.OpCode == OpCodes.Callvirt)
                    && instrucao.Operand is MethodReference alvo
                    && alvo.Name == "IgnoreQueryFilters"
                    && alvo.DeclaringType.FullName == EfQueryableExtensions)
                {
                    chamadas.Add((metodo.FullName, alvo.Parameters.Count));
                }
            }
        }

        return chamadas;
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

    private static string CaminhoDoAssembly()
    {
        string caminho = Path.Combine(AppContext.BaseDirectory, PafAssemblyFile);
        File.Exists(caminho).ShouldBeTrue($"{PafAssemblyFile} not found next to the tests; the scan would pass on nothing");
        return caminho;
    }
}
