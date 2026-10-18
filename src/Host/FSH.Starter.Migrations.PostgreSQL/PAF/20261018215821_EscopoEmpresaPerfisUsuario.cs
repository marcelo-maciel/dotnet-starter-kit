using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSH.Starter.Migrations.PostgreSQL.PAF
{
    /// <inheritdoc />
    public partial class EscopoEmpresaPerfisUsuario : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PerfisUsuario",
                schema: "paf",
                columns: table => new
                {
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    EscopoEmpresa = table.Column<bool>(type: "boolean", nullable: false),
                    Funcao = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Ramal = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerfisUsuario", x => x.UsuarioId);
                });

            // One principal per user (RF08), checked at commit so a reorder swapping two positions passes. Outside the
            // EF model on purpose: DEFERRABLE exists only on a constraint, and as a model index EF refuses the swap.
            migrationBuilder.Sql("""
                ALTER TABLE paf."UsuariosUnidade"
                    ADD CONSTRAINT "UX_UsuariosUnidade_Ordem" UNIQUE ("UsuarioId", "Ordem", "TenantId")
                    DEFERRABLE INITIALLY DEFERRED;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""ALTER TABLE paf."UsuariosUnidade" DROP CONSTRAINT "UX_UsuariosUnidade_Ordem";""");

            migrationBuilder.DropTable(
                name: "PerfisUsuario",
                schema: "paf");
        }
    }
}
