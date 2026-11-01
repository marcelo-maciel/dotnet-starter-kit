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
            // The tenant catalog lives only in the shared database: a tenant with its own connection string runs the
            // Identity migrations in a database without tenant."Tenants", where the rename is skipped instead of
            // failing the provisioning. The FirstName/LastName guard leaves admins that someone renamed untouched.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                  IF to_regclass('tenant."Tenants"') IS NOT NULL THEN
                    UPDATE identity."Users" u
                    SET "FirstName" = 'Administrador', "LastName" = coalesce(btrim(t."Name"), t."Id")
                    FROM tenant."Tenants" t
                    WHERE u."TenantId" = t."Id"
                      AND u."NormalizedUserName" = upper(t."Id" || '.Admin')
                      AND u."FirstName" = upper(t."Id")
                      AND u."LastName" = 'Admin';
                  END IF;
                END $$;
                """);

            migrationBuilder.Sql("""UPDATE identity."Users" SET "Locale" = 'pt-BR' WHERE "Locale" IS NULL;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: the old names and the NULL locales are not restorable.
        }
    }
}
