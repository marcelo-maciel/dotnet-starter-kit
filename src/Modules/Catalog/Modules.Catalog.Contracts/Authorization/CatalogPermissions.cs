using FSH.Framework.Shared.Constants;

namespace FSH.Modules.Catalog.Contracts.Authorization;

public static class CatalogPermissions
{
    public static class Brands
    {
        public const string Resource = "Catalog.Brands";
        public const string View    = $"Permissions.{Resource}.View";
        public const string Create  = $"Permissions.{Resource}.Create";
        public const string Update  = $"Permissions.{Resource}.Update";
        public const string Delete  = $"Permissions.{Resource}.Delete";
        public const string Restore = $"Permissions.{Resource}.Restore";
    }

    public static class Categories
    {
        public const string Resource = "Catalog.Categories";
        public const string View    = $"Permissions.{Resource}.View";
        public const string Create  = $"Permissions.{Resource}.Create";
        public const string Update  = $"Permissions.{Resource}.Update";
        public const string Delete  = $"Permissions.{Resource}.Delete";
        public const string Restore = $"Permissions.{Resource}.Restore";
    }

    public static class Products
    {
        public const string Resource = "Catalog.Products";
        public const string View        = $"Permissions.{Resource}.View";
        public const string Create      = $"Permissions.{Resource}.Create";
        public const string Update      = $"Permissions.{Resource}.Update";
        public const string Delete      = $"Permissions.{Resource}.Delete";
        public const string Restore     = $"Permissions.{Resource}.Restore";
        public const string AdjustStock = $"Permissions.{Resource}.AdjustStock";
    }

    // Root only: the catalog is not part of the PAF product (31/10/2026), so a client's roles never reach it.
    public static IReadOnlyList<FshPermission> All { get; } =
    [
        new("View Brands",    ActionConstants.View,   Brands.Resource, IsBasic: true, IsRoot: true),
        new("Create Brands",  ActionConstants.Create, Brands.Resource, IsRoot: true),
        new("Update Brands",  ActionConstants.Update, Brands.Resource, IsRoot: true),
        new("Delete Brands",  ActionConstants.Delete, Brands.Resource, IsRoot: true),
        new("Restore Brands", "Restore",              Brands.Resource, IsRoot: true),

        new("View Categories",    ActionConstants.View,   Categories.Resource, IsBasic: true, IsRoot: true),
        new("Create Categories",  ActionConstants.Create, Categories.Resource, IsRoot: true),
        new("Update Categories",  ActionConstants.Update, Categories.Resource, IsRoot: true),
        new("Delete Categories",  ActionConstants.Delete, Categories.Resource, IsRoot: true),
        new("Restore Categories", "Restore",              Categories.Resource, IsRoot: true),

        new("View Products",        ActionConstants.View,   Products.Resource, IsBasic: true, IsRoot: true),
        new("Create Products",      ActionConstants.Create, Products.Resource, IsRoot: true),
        new("Update Products",      ActionConstants.Update, Products.Resource, IsRoot: true),
        new("Delete Products",      ActionConstants.Delete, Products.Resource, IsRoot: true),
        new("Restore Products",     "Restore",              Products.Resource, IsRoot: true),
        new("Adjust Product Stock", "AdjustStock",          Products.Resource, IsRoot: true),
    ];
}
