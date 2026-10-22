using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSH.Starter.Migrations.PostgreSQL.PAF
{
    /// <inheritdoc />
    public partial class IndiceCandidatosAtendimentos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Atendimentos_Candidatos",
                schema: "paf",
                table: "Atendimentos",
                columns: new[] { "UnidadeId", "FilaId", "DataElegibilidade" },
                filter: "\"Estado\" = 'Aguardando'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Atendimentos_Candidatos",
                schema: "paf",
                table: "Atendimentos");
        }
    }
}
