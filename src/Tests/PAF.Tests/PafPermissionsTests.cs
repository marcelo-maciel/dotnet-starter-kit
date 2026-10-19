using System.Reflection;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Provisionamento;

namespace PAF.Tests;

public sealed class PafPermissionsTests
{
    // Endpoints authorize by the constant while roles are seeded from All: a constant missing from All
    // can never be granted, and an All entry with no constant has an action string nobody checks.
    private static readonly string[] Constantes = typeof(PafPermissions)
        .GetNestedTypes(BindingFlags.Public)
        .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
        .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name != "Resource")
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();

    private static readonly string[] NomesEmAll = PafPermissions.All.Select(p => p.Name).ToArray();

    [Fact]
    public void All_Should_ContainEveryConstant()
    {
        Constantes.Except(NomesEmAll, StringComparer.Ordinal).ShouldBeEmpty();
    }

    [Fact]
    public void All_Should_HaveOnlyEntriesBackedByConstant()
    {
        NomesEmAll.Except(Constantes, StringComparer.Ordinal).ShouldBeEmpty();
    }

    [Fact]
    public void All_Should_NotRepeatName()
    {
        // PermissionConstants.Register drops duplicates by name silently, hiding the second entry's flags.
        NomesEmAll.Length.ShouldBe(NomesEmAll.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Basicas_Should_BeExactlyTheAttendantSet()
    {
        string[] basicas = PafPermissions.All.Where(p => p.IsBasic).Select(p => p.Name).ToArray();

        basicas.ShouldBe(
        [
            PafPermissions.Guiches.Selecionar,
            PafPermissions.Atendimentos.Operar,
            PafPermissions.Atendimentos.Encaminhar,
            PafPermissions.Atendimentos.SelecionarFicha,
            PafPermissions.Fichas.Emitir,
        ], ignoreOrder: true);
    }

    [Fact]
    public void Raiz_Should_BeOperatorOnly()
    {
        string[] raiz = PafPermissions.All.Where(p => p.IsRoot).Select(p => p.Name).ToArray();

        raiz.ShouldBe(
        [
            PafPermissions.Contratos.Manage,
            PafPermissions.Unidades.Operar,
        ], ignoreOrder: true);
    }

    // D10 (17/10/2026): the type 4 report per atendente goes to the client's Gerente and Administrador, never to the
    // Atendente, and it must not be root, or the kit strips it from every client role.
    [Fact]
    public void ViewPorAtendente_Should_BeGrantedToGerenteAndAdministradorOnly()
    {
        PapeisPadrao.PermissoesGerente.ShouldContain(PafPermissions.Estatisticas.ViewPorAtendente);
        PapeisPadrao.PermissoesAdministrador.ShouldContain(PafPermissions.Estatisticas.ViewPorAtendente);
        PapeisPadrao.PermissoesAtendente.ShouldNotContain(PafPermissions.Estatisticas.ViewPorAtendente);
        PafPermissions.All.Single(p => p.Name == PafPermissions.Estatisticas.ViewPorAtendente).IsRoot.ShouldBeFalse();
    }

    [Fact]
    public void Raiz_Should_NeverBeBasic()
    {
        PafPermissions.All.Where(p => p.IsRoot && p.IsBasic).ShouldBeEmpty();
    }

    [Fact]
    public void Catalogo_Should_ContainSlice2Constants()
    {
        // Guards against the reflection scan finding nothing (e.g. nested classes turned non-public), which would make the two inclusion tests pass vacuously.
        Constantes.ShouldContain(PafPermissions.Programacoes.Delete);
        Constantes.ShouldContain(PafPermissions.Parametros.UpdateEmpresa);
        Constantes.ShouldContain(PafPermissions.Usuarios.Manage);
        Constantes.ShouldContain(PafPermissions.FichasEmAtendimento.View);
        Constantes.ShouldContain(PafPermissions.Atendimentos.Vip);
        Constantes.ShouldContain(PafPermissions.Guiches.Liberar);
    }
}
