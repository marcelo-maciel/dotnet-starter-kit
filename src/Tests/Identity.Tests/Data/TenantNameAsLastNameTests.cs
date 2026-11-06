using FSH.Modules.Identity.Data;

namespace Identity.Tests.Data;

/// <summary>
/// The seeded tenant admin's last name is the client's name cut to the profile validator's 50 characters, without
/// leaving half of a surrogate pair (Npgsql refuses a lone surrogate) or a trailing space behind.
/// </summary>
public sealed class TenantNameAsLastNameTests
{
    [Fact]
    public void EmojiStraddlingTheLimit_IsDroppedWhole_NotSplit()
    {
        // 49 letters, then an emoji whose high surrogate is char 50 and low surrogate char 51.
        var name = new string('a', 49) + "\U0001F600" + " Filial";

        var lastName = IdentityDbInitializer.TenantNameAsLastName(name);

        lastName.ShouldBe(new string('a', 49));
        lastName!.Any(char.IsSurrogate).ShouldBeFalse();
    }

    [Fact]
    public void EmojiEndingExactlyAtTheLimit_IsKept()
    {
        var name = new string('a', 48) + "\U0001F600" + "bcd";

        var lastName = IdentityDbInitializer.TenantNameAsLastName(name);

        lastName.ShouldBe(new string('a', 48) + "\U0001F600");
        lastName!.Length.ShouldBe(IdentityDbInitializer.LastNameMaxLength);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  Loja Centro  ", "Loja Centro")]
    public void ShortOrMissingName_IsOnlyTrimmed(string? name, string? expected)
    {
        IdentityDbInitializer.TenantNameAsLastName(name).ShouldBe(expected);
    }

    [Fact]
    public void LongName_IsCutAtTheLimit_WithoutATrailingSpace()
    {
        var name = new string('a', 49) + " " + new string('b', 20);

        IdentityDbInitializer.TenantNameAsLastName(name).ShouldBe(new string('a', 49));
    }
}
