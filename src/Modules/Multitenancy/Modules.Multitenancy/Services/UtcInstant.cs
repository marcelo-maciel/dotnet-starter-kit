namespace FSH.Modules.Multitenancy.Services;

/// <summary>
/// Normalizes a <see cref="DateTime"/> that reaches the module as a UTC instant. System.Text.Json reads a
/// value with a non-<c>Z</c> offset ("2026-05-01T00:00:00-03:00") as <see cref="DateTimeKind.Local"/>,
/// already moved to the container's zone, so relabelling it as UTC is right only when that zone is UTC.
/// A Local value is converted; an Unspecified one is UTC by contract. Billing keeps the same helper,
/// since a module cannot reference another's runtime project.
/// </summary>
internal static class UtcInstant
{
    public static DateTime From(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
