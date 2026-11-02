using FSH.Framework.Shared.Constants;

namespace FSH.Modules.Identity.Contracts.Authorization;

/// <summary>
/// Identity module permissions. Single source of truth — string literals (used by
/// <c>.RequirePermission(...)</c>) and the <see cref="All"/> registry list are derived
/// from the same Resource/Action constants below, so they cannot drift.
/// </summary>
public static class IdentityPermissions
{
    public static class Users
    {
        public const string Resource = nameof(Users);
        public const string View          = $"Permissions.{Resource}.View";
        public const string Search        = $"Permissions.{Resource}.Search";
        public const string Create        = $"Permissions.{Resource}.Create";
        public const string Update        = $"Permissions.{Resource}.Update";
        public const string Delete        = $"Permissions.{Resource}.Delete";
        public const string Export        = $"Permissions.{Resource}.Export";
        public const string ManageRoles   = $"Permissions.{Resource}.ManageRoles";
        public const string Impersonate   = $"Permissions.{Resource}.Impersonate";
        public const string ConfirmEmail  = $"Permissions.{Resource}.ConfirmEmail";
    }

    public static class UserRoles
    {
        public const string Resource = nameof(UserRoles);
        public const string View   = $"Permissions.{Resource}.View";
        public const string Update = $"Permissions.{Resource}.Update";
    }

    public static class Roles
    {
        public const string Resource = nameof(Roles);
        public const string View   = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Update = $"Permissions.{Resource}.Update";
        public const string Delete = $"Permissions.{Resource}.Delete";
    }

    public static class RoleClaims
    {
        public const string Resource = nameof(RoleClaims);
        public const string View   = $"Permissions.{Resource}.View";
        public const string Update = $"Permissions.{Resource}.Update";
    }

    public static class Sessions
    {
        public const string Resource = nameof(Sessions);
        public const string View      = $"Permissions.{Resource}.View";
        public const string Revoke    = $"Permissions.{Resource}.Revoke";
        public const string ViewAll   = $"Permissions.{Resource}.ViewAll";
        public const string RevokeAll = $"Permissions.{Resource}.RevokeAll";
    }

    public static class Groups
    {
        public const string Resource = nameof(Groups);
        public const string View          = $"Permissions.{Resource}.View";
        public const string Create        = $"Permissions.{Resource}.Create";
        public const string Update        = $"Permissions.{Resource}.Update";
        public const string Delete        = $"Permissions.{Resource}.Delete";
        public const string ManageMembers = $"Permissions.{Resource}.ManageMembers";
    }

    public static class Impersonation
    {
        public const string Resource = nameof(Impersonation);
        /// <summary>List impersonation grants (read-only access to the grant history).</summary>
        public const string View   = $"Permissions.{Resource}.View";
        /// <summary>Revoke an active impersonation grant before its natural expiry.</summary>
        public const string Revoke = $"Permissions.{Resource}.Revoke";
    }

    public static IReadOnlyList<FshPermission> All { get; } =
    [
        // Users, Roles and Groups are root only (02/11/2026): a client manages its users on /paf/usuarios, which keeps
        // the unidade scope, revokes sessions on deactivation and cleans the PAF data; the kit screens and endpoints
        // did none of that. Search, Export, UserRoles and RoleClaims gate no endpoint, so they stay as they were.
        new("View Users",          ActionConstants.View,   Users.Resource, IsBasic: true, IsRoot: true),
        new("Search Users",        ActionConstants.Search, Users.Resource),
        new("Create Users",        ActionConstants.Create, Users.Resource, IsRoot: true),
        new("Update Users",        ActionConstants.Update, Users.Resource, IsRoot: true),
        new("Delete Users",        ActionConstants.Delete, Users.Resource, IsRoot: true),
        new("Export Users",        ActionConstants.Export, Users.Resource),
        new("Manage User Roles",   "ManageRoles",          Users.Resource, IsRoot: true),
        // Root only: acting as a client's user is support done by the Codefi operator, never by the client's own admin.
        new("Impersonate User",    "Impersonate",          Users.Resource, IsRoot: true),
        new("Confirm User Email",  "ConfirmEmail",         Users.Resource, IsRoot: true),

        new("View User Roles",     ActionConstants.View,   UserRoles.Resource, IsBasic: true),
        new("Update User Roles",   ActionConstants.Update, UserRoles.Resource),

        new("View Roles",          ActionConstants.View,   Roles.Resource, IsBasic: true, IsRoot: true),
        new("Create Roles",        ActionConstants.Create, Roles.Resource, IsRoot: true),
        new("Update Roles",        ActionConstants.Update, Roles.Resource, IsRoot: true),
        new("Delete Roles",        ActionConstants.Delete, Roles.Resource, IsRoot: true),

        new("View Role Claims",    ActionConstants.View,   RoleClaims.Resource, IsBasic: true),
        new("Update Role Claims",  ActionConstants.Update, RoleClaims.Resource),

        new("View My Sessions",    ActionConstants.View,    Sessions.Resource, IsBasic: true),
        new("Revoke My Sessions",  "Revoke",                Sessions.Resource, IsBasic: true),
        new("View All Sessions",   "ViewAll",               Sessions.Resource),
        new("Revoke Any Session",  "RevokeAll",             Sessions.Resource),

        new("View Groups",         ActionConstants.View,   Groups.Resource, IsBasic: true, IsRoot: true),
        new("Create Groups",       ActionConstants.Create, Groups.Resource, IsRoot: true),
        new("Update Groups",       ActionConstants.Update, Groups.Resource, IsRoot: true),
        new("Delete Groups",       ActionConstants.Delete, Groups.Resource, IsRoot: true),
        new("Manage Group Members","ManageMembers",        Groups.Resource, IsRoot: true),

        new("View Impersonation Grants",   ActionConstants.View, Impersonation.Resource),
        new("Revoke Impersonation Grants", "Revoke",             Impersonation.Resource),
    ];
}
