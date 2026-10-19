using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Features.v1.Usuarios.Permissoes;

namespace PAF.Tests;

/// <summary>RF08 (C15): the four blocks of the user screen cover the client-side PAF catalog, each permission once.</summary>
public sealed class GruposPermissoesPafTests
{
    private static readonly string[] NosGrupos =
        [.. GruposPermissoesPaf.Todos.SelectMany(g => g.Permissoes).Select(p => p.Nome)];

    [Fact]
    public void Grupos_SaoOsQuatroDoRf08_NaOrdem()
    {
        GruposPermissoesPaf.Todos.Select(g => g.Nome).ShouldBe(
            [GruposPermissoesPaf.ControleDeFila, GruposPermissoesPaf.Atendimento, GruposPermissoesPaf.PainelChamador, GruposPermissoesPaf.Administracao]);
    }

    [Fact]
    public void Grupos_CobremTodaPermissaoNaoRaizDeAll_UmaVezSo()
    {
        var naoRaiz = PafPermissions.All.Where(p => !p.IsRoot).Select(p => p.Name).ToArray();

        NosGrupos.Length.ShouldBe(NosGrupos.Distinct(StringComparer.Ordinal).Count(), "uma permissão em dois grupos");
        naoRaiz.Except(NosGrupos, StringComparer.Ordinal).ShouldBeEmpty("permissão do cliente fora dos quatro grupos");
        NosGrupos.Except(naoRaiz, StringComparer.Ordinal).ShouldBeEmpty("permissão raiz ou inexistente num grupo");
    }

    [Fact]
    public void Grupos_TodaPermissaoTemRotulo()
    {
        GruposPermissoesPaf.Todos.SelectMany(g => g.Permissoes).ShouldAllBe(p => !string.IsNullOrWhiteSpace(p.Rotulo));
    }

    [Fact]
    public void NomeDoPapelPessoal_NaoCarregaDadoPessoalESeparaUsuarios()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        PermissoesIndividuais.NomeDoPapel(a).ShouldBe($"PAF-usuario-{a:N}");
        PermissoesIndividuais.NomeDoPapel(a).ShouldNotBe(PermissoesIndividuais.NomeDoPapel(b));
    }
}
