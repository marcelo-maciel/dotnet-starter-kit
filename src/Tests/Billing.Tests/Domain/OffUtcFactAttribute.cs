namespace Billing.Tests.Domain;

/// <summary>
/// A fact that runs only where the machine zone is not UTC, and is reported as skipped elsewhere. On a
/// UTC machine a Local DateTime has the UTC wall clock, so relabelling it as UTC is right there and a test
/// of the conversion would pass whatever the code does. CI sets <c>TZ=America/Sao_Paulo</c> for the unit
/// tests (<c>.github/workflows/backend.yml</c>). xUnit 2 has no runtime skip, hence the attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class OffUtcFactAttribute : FactAttribute
{
    public OffUtcFactAttribute()
    {
        if (TimeZoneInfo.Local.BaseUtcOffset == TimeSpan.Zero)
        {
            Skip = "The machine zone is UTC, where a Local DateTime cannot tell a conversion from a relabel.";
        }
    }
}
