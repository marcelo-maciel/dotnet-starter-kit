namespace FSH.Framework.Eventing;

/// <summary>
/// Constants for the framework-owned eventing store.
/// </summary>
public static class EventingConstants
{
    /// <summary>
    /// Database schema owning <c>OutboxMessages</c> and <c>InboxMessages</c>.
    /// These tables are framework infrastructure, not module data, so they live
    /// outside every module schema.
    /// </summary>
    public const string SchemaName = "framework";
}
