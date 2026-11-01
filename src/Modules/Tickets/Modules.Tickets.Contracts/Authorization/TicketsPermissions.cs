using FSH.Framework.Shared.Constants;

namespace FSH.Modules.Tickets.Contracts.Authorization;

public static class TicketsPermissions
{
    public static class Tickets
    {
        public const string Resource = "Tickets";
        public const string View     = $"Permissions.{Resource}.View";
        public const string Create   = $"Permissions.{Resource}.Create";
        public const string Update   = $"Permissions.{Resource}.Update";
        public const string Delete   = $"Permissions.{Resource}.Delete";
        public const string Restore  = $"Permissions.{Resource}.Restore";
        public const string Assign   = $"Permissions.{Resource}.Assign";
        public const string Resolve  = $"Permissions.{Resource}.Resolve";
        public const string Reopen   = $"Permissions.{Resource}.Reopen";
        public const string Close    = $"Permissions.{Resource}.Close";
        public const string Comment  = $"Permissions.{Resource}.Comment";
    }

    // Root only: the helpdesk is not part of the PAF product (31/10/2026), so a client's roles never reach it.
    public static IReadOnlyList<FshPermission> All { get; } =
    [
        new("View Tickets",    ActionConstants.View,   Tickets.Resource, IsBasic: true, IsRoot: true),
        new("Create Tickets",  ActionConstants.Create, Tickets.Resource, IsRoot: true),
        new("Update Tickets",  ActionConstants.Update, Tickets.Resource, IsRoot: true),
        new("Delete Tickets",  ActionConstants.Delete, Tickets.Resource, IsRoot: true),
        new("Restore Tickets", "Restore", Tickets.Resource, IsRoot: true),
        new("Assign Tickets",  "Assign",  Tickets.Resource, IsRoot: true),
        new("Resolve Tickets", "Resolve", Tickets.Resource, IsRoot: true),
        new("Reopen Tickets",  "Reopen",  Tickets.Resource, IsRoot: true),
        new("Close Tickets",   "Close",   Tickets.Resource, IsRoot: true),
        new("Comment on Tickets", "Comment", Tickets.Resource, IsRoot: true),
    ];
}
