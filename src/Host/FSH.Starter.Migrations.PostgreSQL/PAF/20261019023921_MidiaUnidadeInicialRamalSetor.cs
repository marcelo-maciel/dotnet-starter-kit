using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSH.Starter.Migrations.PostgreSQL.PAF
{
    /// <inheritdoc />
    public partial class MidiaUnidadeInicialRamalSetor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Setores_UnidadeId",
                schema: "paf",
                table: "Setores");

            migrationBuilder.DropIndex(
                name: "IX_Estacoes_UnidadeId",
                schema: "paf",
                table: "Estacoes");

            migrationBuilder.AddColumn<bool>(
                name: "Inicial",
                schema: "paf",
                table: "Unidades",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Ramal",
                schema: "paf",
                table: "Estacoes",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Estacoes_UnidadeId_Id",
                schema: "paf",
                table: "Estacoes",
                columns: new[] { "UnidadeId", "Id" });

            migrationBuilder.CreateTable(
                name: "EnviosMidia",
                schema: "paf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UnidadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChaveObjeto = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Tipo = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CriadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ConfirmadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RecusadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RemovidoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnviosMidia", x => x.Id);
                    table.UniqueConstraint("AK_EnviosMidia_UnidadeId_Id_ChaveObjeto", x => new { x.UnidadeId, x.Id, x.ChaveObjeto });
                    table.CheckConstraint("CK_EnviosMidia_Desfecho", "\"ConfirmadoEm\" IS NULL OR \"RecusadoEm\" IS NULL");
                    table.CheckConstraint("CK_EnviosMidia_Tipo", "\"Tipo\" IN ('Video', 'Imagem', 'Audio')");
                    table.ForeignKey(
                        name: "FK_EnviosMidia_Unidades_UnidadeId",
                        column: x => x.UnidadeId,
                        principalSchema: "paf",
                        principalTable: "Unidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Midias",
                schema: "paf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UnidadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Descricao = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Tipo = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EnvioId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChaveObjeto = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    DuracaoSegundos = table.Column<int>(type: "integer", nullable: false),
                    Ativa = table.Column<bool>(type: "boolean", nullable: false),
                    Versao = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CriadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Midias", x => x.Id);
                    table.UniqueConstraint("AK_Midias_UnidadeId_Id", x => new { x.UnidadeId, x.Id });
                    table.CheckConstraint("CK_Midias_Duracao", "\"DuracaoSegundos\" BETWEEN 1 AND 86400");
                    table.CheckConstraint("CK_Midias_Tipo", "\"Tipo\" IN ('Video', 'Imagem', 'Audio')");
                    table.ForeignKey(
                        name: "FK_Midias_EnviosMidia_MesmaUnidade",
                        columns: x => new { x.UnidadeId, x.EnvioId, x.ChaveObjeto },
                        principalSchema: "paf",
                        principalTable: "EnviosMidia",
                        principalColumns: new[] { "UnidadeId", "Id", "ChaveObjeto" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Midias_Unidades_UnidadeId",
                        column: x => x.UnidadeId,
                        principalSchema: "paf",
                        principalTable: "Unidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PainelMidias",
                schema: "paf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UnidadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    EstacaoId = table.Column<Guid>(type: "uuid", nullable: false),
                    MidiaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordem = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PainelMidias", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PainelMidias_Estacoes_MesmaUnidade",
                        columns: x => new { x.UnidadeId, x.EstacaoId },
                        principalSchema: "paf",
                        principalTable: "Estacoes",
                        principalColumns: new[] { "UnidadeId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PainelMidias_Midias_MesmaUnidade",
                        columns: x => new { x.UnidadeId, x.MidiaId },
                        principalSchema: "paf",
                        principalTable: "Midias",
                        principalColumns: new[] { "UnidadeId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PainelMidias_Unidades_UnidadeId",
                        column: x => x.UnidadeId,
                        principalSchema: "paf",
                        principalTable: "Unidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProgramacoesMidia",
                schema: "paf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UnidadeId = table.Column<Guid>(type: "uuid", nullable: false),
                    MidiaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Nome = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    DataInicial = table.Column<DateOnly>(type: "date", nullable: false),
                    DataFinal = table.Column<DateOnly>(type: "date", nullable: false),
                    HoraInicial = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    Repeticoes = table.Column<int>(type: "integer", nullable: false),
                    Dias = table.Column<int>(type: "integer", nullable: false),
                    Ativa = table.Column<bool>(type: "boolean", nullable: false),
                    Versao = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CriadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProgramacoesMidia", x => x.Id);
                    table.CheckConstraint("CK_ProgramacoesMidia_Dias", "\"Dias\" BETWEEN 1 AND 127");
                    table.CheckConstraint("CK_ProgramacoesMidia_Periodo", "\"DataFinal\" >= \"DataInicial\"");
                    table.CheckConstraint("CK_ProgramacoesMidia_Repeticoes", "\"Repeticoes\" >= 1");
                    table.ForeignKey(
                        name: "FK_ProgramacoesMidia_Midias_MesmaUnidade",
                        columns: x => new { x.UnidadeId, x.MidiaId },
                        principalSchema: "paf",
                        principalTable: "Midias",
                        principalColumns: new[] { "UnidadeId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProgramacoesMidia_Unidades_UnidadeId",
                        column: x => x.UnidadeId,
                        principalSchema: "paf",
                        principalTable: "Unidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_Unidades_Inicial",
                schema: "paf",
                table: "Unidades",
                columns: new[] { "Inicial", "TenantId" },
                unique: true,
                filter: "\"Inicial\"");

            migrationBuilder.CreateIndex(
                name: "UX_Setores_Nome",
                schema: "paf",
                table: "Setores",
                columns: new[] { "UnidadeId", "Nome", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_EnviosMidia_ChaveObjeto",
                schema: "paf",
                table: "EnviosMidia",
                columns: new[] { "ChaveObjeto", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Midias_UnidadeId_EnvioId_ChaveObjeto",
                schema: "paf",
                table: "Midias",
                columns: new[] { "UnidadeId", "EnvioId", "ChaveObjeto" });

            migrationBuilder.CreateIndex(
                name: "UX_Midias_ChaveObjeto",
                schema: "paf",
                table: "Midias",
                columns: new[] { "ChaveObjeto", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PainelMidias_UnidadeId_EstacaoId",
                schema: "paf",
                table: "PainelMidias",
                columns: new[] { "UnidadeId", "EstacaoId" });

            migrationBuilder.CreateIndex(
                name: "IX_PainelMidias_UnidadeId_MidiaId",
                schema: "paf",
                table: "PainelMidias",
                columns: new[] { "UnidadeId", "MidiaId" });

            migrationBuilder.CreateIndex(
                name: "UX_PainelMidias_Ordem",
                schema: "paf",
                table: "PainelMidias",
                columns: new[] { "EstacaoId", "Ordem", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProgramacoesMidia_UnidadeId_MidiaId",
                schema: "paf",
                table: "ProgramacoesMidia",
                columns: new[] { "UnidadeId", "MidiaId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PainelMidias",
                schema: "paf");

            migrationBuilder.DropTable(
                name: "ProgramacoesMidia",
                schema: "paf");

            migrationBuilder.DropTable(
                name: "Midias",
                schema: "paf");

            migrationBuilder.DropTable(
                name: "EnviosMidia",
                schema: "paf");

            migrationBuilder.DropIndex(
                name: "UX_Unidades_Inicial",
                schema: "paf",
                table: "Unidades");

            migrationBuilder.DropIndex(
                name: "UX_Setores_Nome",
                schema: "paf",
                table: "Setores");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Estacoes_UnidadeId_Id",
                schema: "paf",
                table: "Estacoes");

            migrationBuilder.DropColumn(
                name: "Inicial",
                schema: "paf",
                table: "Unidades");

            migrationBuilder.DropColumn(
                name: "Ramal",
                schema: "paf",
                table: "Estacoes");

            migrationBuilder.CreateIndex(
                name: "IX_Setores_UnidadeId",
                schema: "paf",
                table: "Setores",
                column: "UnidadeId");

            migrationBuilder.CreateIndex(
                name: "IX_Estacoes_UnidadeId",
                schema: "paf",
                table: "Estacoes",
                column: "UnidadeId");
        }
    }
}
