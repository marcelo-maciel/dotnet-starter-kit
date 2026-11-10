using FSH.Modules.Identity.Services;

namespace Identity.Tests.Services;

/// <summary>
/// Tests for EmailAddressMatch - same mailbox only up to ASCII case, never through the wider Unicode folding
/// the normalized e-mail applies.
/// </summary>
public sealed class EmailAddressMatchTests
{
    #region Happy Path

    [Theory]
    [InlineData("susan@corp.com", "susan@corp.com")]
    [InlineData("Susan@Corp.COM", "susan@corp.com")]
    [InlineData("jos\u00E9@corp.com", "jos\u00E9@corp.com")]
    public void IsSameAddress_Should_ReturnTrue_When_OnlyAsciiCaseDiffers(string first, string second)
    {
        EmailAddressMatch.IsSameAddress(first, second).ShouldBeTrue();
        EmailAddressMatch.IsSameAddress(second, first).ShouldBeTrue();
    }

    #endregion

    #region Edge Cases

    // The decomposed e-acute does not match the composed one: NFC, which would join them, also turns the
    // Kelvin sign into K.
    [Theory]
    [InlineData("\u017Fusan@corp.com", "susan@corp.com")]
    [InlineData("\u017Fusan@corp.com", "Susan@corp.com")]
    [InlineData("\u212Aate@corp.com", "kate@corp.com")]
    [InlineData("jos\u00C9@corp.com", "jos\u00E9@corp.com")]
    [InlineData("jose\u0301@corp.com", "jos\u00E9@corp.com")]
    [InlineData("susan@corp.com", "susan@corp.co")]
    [InlineData("susan@corp.com", "susan@corp.con")]
    [InlineData("a[b@corp.com", "a{b@corp.com")]
    public void IsSameAddress_Should_ReturnFalse_When_ANonAsciiCharacterOrItsCompositionDiffers(string first, string second)
    {
        EmailAddressMatch.IsSameAddress(first, second).ShouldBeFalse();
        EmailAddressMatch.IsSameAddress(second, first).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null, "susan@corp.com")]
    [InlineData("susan@corp.com", null)]
    [InlineData(null, null)]
    public void IsSameAddress_Should_ReturnFalse_When_EitherIsNull(string? first, string? second)
    {
        EmailAddressMatch.IsSameAddress(first, second).ShouldBeFalse();
    }

    #endregion
}
