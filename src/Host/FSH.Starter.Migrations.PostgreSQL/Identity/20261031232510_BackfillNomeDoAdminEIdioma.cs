using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSH.Starter.Migrations.PostgreSQL.Identity
{
    /// <inheritdoc />
    public partial class BackfillNomeDoAdminEIdioma : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Needs tenant."Tenants": the migrator applies the tenant catalog before any per-tenant context.
            // The FirstName/LastName guard leaves admins that someone renamed untouched.
            migrationBuilder.Sql(
                """
                UPDATE identity."Users" u
                SET "FirstName" = 'Administrador', "LastName" = coalesce(btrim(t."Name"), t."Id")
                FROM tenant."Tenants" t
                WHERE u."TenantId" = t."Id"
                  AND u."NormalizedUserName" = upper(t."Id" || '.Admin')
                  AND u."FirstName" = upper(t."Id")
                  AND u."LastName" = 'Admin';

                UPDATE identity."Users" SET "Locale" = 'pt-BR' WHERE "Locale" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: the old names and the NULL locales are not restorable.
        }
    }
}
