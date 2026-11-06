using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSH.Starter.Migrations.PostgreSQL.PAF
{
    /// <inheritdoc />
    public partial class ConsumoLicenca : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConsumosLicenca",
                schema: "paf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UnidadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tipo = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Ocupadas = table.Column<int>(type: "integer", nullable: false),
                    Limite = table.Column<int>(type: "integer", nullable: false),
                    SessaoId = table.Column<Guid>(type: "uuid", nullable: true),
                    RegistradoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsumosLicenca", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConsumosLicenca_Unidades_UnidadeId",
                        column: x => x.UnidadeId,
                        principalSchema: "paf",
                        principalTable: "Unidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConsumosLicenca_Sessao",
                schema: "paf",
                table: "ConsumosLicenca",
                column: "SessaoId");

            migrationBuilder.CreateIndex(
                name: "UX_ConsumosLicenca_Amostra",
                schema: "paf",
                table: "ConsumosLicenca",
                columns: new[] { "UnidadeId", "RegistradoEm", "TenantId" },
                unique: true,
                filter: "\"Tipo\" = 'Amostra'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConsumosLicenca",
                schema: "paf");
        }
    }
}
