namespace FSH.Framework.Core.Localization;

/// <summary>Canonical set of cultures the platform supports for user-facing localization.</summary>
public static class SupportedCultures
{
    /// <summary>Guaranteed ultimate fallback culture (neutral catalog).</summary>
    public const string Default = "en-US";

    /// <summary>Specific tags a user may persist and the switcher offers.</summary>
    public static readonly string[] Tags = ["en-US", "pt-BR"];

    /// <summary>Tags for Accept-Language matching, including neutrals for parent-culture fallback.</summary>
    public static readonly string[] RequestMatch = ["en-US", "pt-BR", "pt", "en"];
}
