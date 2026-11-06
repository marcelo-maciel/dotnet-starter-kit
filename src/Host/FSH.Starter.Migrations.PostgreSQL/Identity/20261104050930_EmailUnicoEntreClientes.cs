using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSH.Starter.Migrations.PostgreSQL.Identity
{
    /// <inheritdoc />
    public partial class EmailUnicoEntreClientes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedEmail_Clientes",
                schema: "identity",
                table: "Users",
                column: "NormalizedEmail",
                unique: true,
                filter: "\"TenantId\" <> 'root'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_NormalizedEmail_Clientes",
                schema: "identity",
                table: "Users");
        }
    }
}
