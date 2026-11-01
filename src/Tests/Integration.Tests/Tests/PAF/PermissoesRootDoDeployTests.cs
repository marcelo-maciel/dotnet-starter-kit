using FSH.Framework.Shared.Constants;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// deploy.sh step 7 reads deploy/paf/permissoes-root.txt to find a root-only permission left on a client's role. A
/// permission made root-only in C# but missing from the file would pass that check unseen, so the file must hold exactly
/// the registry's root-only set. The collection fixture starts the host, which is what fills the registry.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PermissoesRootDoDeployTests
{
    [Fact]
    public void ListaDoDeploy_Should_BeTheRegistryRootOnlySet()
    {
        var arquivo = Path.Combine(RaizDoRepositorio(), "deploy", "paf", "permissoes-root.txt");
        var doDeploy = File.ReadAllLines(arquivo)
            .Select(linha => linha.Trim())
            .Where(linha => linha.Length > 0 && !linha.StartsWith('#'))
            .ToList();
        var doRegistro = PermissionConstants.Root.Select(p => p.Name).ToList();

        doDeploy.ShouldBeUnique();
        doRegistro.ShouldNotBeEmpty();
        doDeploy.Except(doRegistro).ShouldBeEmpty("no arquivo e não root-only no C#");
        doRegistro.Except(doDeploy).ShouldBeEmpty("root-only no C# e fora do arquivo");
    }

    private static string RaizDoRepositorio()
    {
        for (var pasta = new DirectoryInfo(AppContext.BaseDirectory); pasta is not null; pasta = pasta.Parent)
        {
            if (File.Exists(Path.Combine(pasta.FullName, "deploy", "paf", "permissoes-root.txt")))
            {
                return pasta.FullName;
            }
        }

        throw new InvalidOperationException($"deploy/paf/permissoes-root.txt não encontrado acima de {AppContext.BaseDirectory}");
    }
}
