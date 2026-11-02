using System.Reflection;
using FSH.Modules.Identity.Localization;
using Identity.Tests.Support;
using Microsoft.AspNetCore.Identity;

namespace Identity.Tests.Services;

/// <summary>
/// The Identity error texts reach the client in the ProblemDetails <c>errors</c> list. Every virtual of the
/// framework describer is exercised by reflection, so a describer method added by a framework upgrade and not
/// overridden here, or a key missing from either catalog, fails instead of leaking English to a pt-BR user.
/// </summary>
public sealed class LocalizedIdentityErrorDescriberTests
{
    private readonly IdentityErrorDescriber _framework = new();
    private readonly LocalizedIdentityErrorDescriber _sut = new(IdentityResourcesLocalizerFactory.Create());

    private static List<string> FrameworkMethodNames() =>
        typeof(IdentityErrorDescriber)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.IsVirtual && m.ReturnType == typeof(IdentityError))
            .Select(m => m.Name)
            .ToList();

    public static TheoryData<string> DescriberMethods() => new(FrameworkMethodNames());

    private static IdentityError Describe(IdentityErrorDescriber describer, string methodName)
    {
        var method = describer.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)!;
        var args = method.GetParameters()
            .Select(p => p.ParameterType == typeof(int) ? (object)8 : "alvo")
            .ToArray();
        return (IdentityError)method.Invoke(describer, args)!;
    }

    [Fact]
    public void Every_framework_describer_method_is_overridden()
    {
        var notOverridden = FrameworkMethodNames()
            .Where(name => typeof(LocalizedIdentityErrorDescriber).GetMethod(name)!.DeclaringType != typeof(LocalizedIdentityErrorDescriber))
            .ToList();

        notOverridden.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(DescriberMethods))]
    public void English_description_matches_the_framework_text(string methodName)
    {
        using var culture = new UiCultureScope("en-US");

        var localized = Describe(_sut, methodName);
        var framework = Describe(_framework, methodName);

        localized.Code.ShouldBe(framework.Code);
        localized.Description.ShouldBe(framework.Description);
    }

    [Theory]
    [MemberData(nameof(DescriberMethods))]
    public void Portuguese_description_comes_from_the_catalog_and_keeps_the_code(string methodName)
    {
        using var culture = new UiCultureScope("pt-BR");

        var localized = Describe(_sut, methodName);
        var framework = Describe(_framework, methodName);

        localized.Code.ShouldBe(framework.Code);
        localized.Description.ShouldNotBe(framework.Description);
        localized.Description.ShouldNotStartWith("Identity.Error.");
        localized.Description.ShouldNotContain("—");
    }

    [Fact]
    public void Portuguese_description_carries_the_argument()
    {
        using var culture = new UiCultureScope("pt-BR");

        _sut.PasswordTooShort(8).Description.ShouldBe("A senha precisa ter ao menos 8 caracteres.");
        _sut.DuplicateEmail("ana@empresa.com").Description.ShouldBe("O e-mail 'ana@empresa.com' já está em uso.");
        _sut.PasswordMismatch().Description.ShouldBe("Senha incorreta.");
    }
}
