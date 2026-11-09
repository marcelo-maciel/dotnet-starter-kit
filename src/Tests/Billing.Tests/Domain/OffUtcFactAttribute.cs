using System.Runtime.CompilerServices;

namespace Billing.Tests.Domain;

/// <summary>
/// A fact that runs only where the machine zone is not UTC, and is reported as skipped elsewhere. On a
/// UTC machine a Local DateTime has the UTC wall clock, so relabelling it as UTC is right there and a test
/// of the conversion would pass whatever the code does. CI sets <c>TZ=America/Sao_Paulo</c> for the unit
/// tests (<c>.github/workflows/backend.yml</c>). The skip is decided where the attribute is built, so no test body repeats it.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class OffUtcFactAttribute : FactAttribute
{
    // xUnit v3 reads the test's source location through these caller-info parameters (xUnit3003).
    public OffUtcFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (TimeZoneInfo.Local.BaseUtcOffset == TimeSpan.Zero)
        {
            Skip = "The machine zone is UTC, where a Local DateTime cannot tell a conversion from a relabel.";
        }
    }
}
