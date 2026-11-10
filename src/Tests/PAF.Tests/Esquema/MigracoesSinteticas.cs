using FSH.Modules.PAF.Data;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PAF.Tests.Esquema;

// Synthetic migrations for BarreiraExpandirContrairTests: never in the migrations assembly, so EF never applies them.
// Column and index names are real ones of the paf schema, so the unique-index lookup runs against the real model.
internal sealed class SinteticaDropColumn : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(name: "Letra", table: "Fichas", schema: PafDbContext.Schema);
}

internal sealed class SinteticaRenameColumn : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.RenameColumn(name: "Letra", table: "Fichas", newName: "Prefixo", schema: PafDbContext.Schema);
}

internal sealed class SinteticaDropTable : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "Fichas", schema: PafDbContext.Schema);
}

internal sealed class SinteticaRenameTable : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.RenameTable(name: "Fichas", schema: PafDbContext.Schema, newName: "FichasEmitidas", newSchema: PafDbContext.Schema);
}

internal sealed class SinteticaNuloParaObrigatorio : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AlterColumn<Guid>(name: "EstacaoId", table: "Atendimentos", schema: PafDbContext.Schema, type: "uuid", nullable: false,
            oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
}

internal sealed class SinteticaObrigatorioParaNulo : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AlterColumn<string>(name: "Letra", table: "Fichas", schema: PafDbContext.Schema, type: "character varying(5)", nullable: true,
            oldClrType: typeof(string), oldType: "character varying(5)", oldNullable: false);
}

internal sealed class SinteticaAddColumnNula : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(name: "Observacao", table: "Fichas", schema: PafDbContext.Schema, type: "character varying(200)", nullable: true);
}

internal sealed class SinteticaAddColumnObrigatoriaSemPadrao : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(name: "Origem", table: "Fichas", schema: PafDbContext.Schema, type: "character varying(16)", nullable: false);
}

internal sealed class SinteticaAddColumnObrigatoriaComPadrao : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<bool>(name: "Reimpressa", table: "Fichas", schema: PafDbContext.Schema, type: "boolean", nullable: false, defaultValue: false);
}

internal sealed class SinteticaCreateTableEIndice : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(name: "Novas", schema: PafDbContext.Schema,
            columns: t => new { Id = t.Column<Guid>(type: "uuid", nullable: false) },
            constraints: t => t.PrimaryKey("PK_Novas", x => x.Id));
        migrationBuilder.CreateIndex(name: "IX_Fichas_Letra", table: "Fichas", schema: PafDbContext.Schema, column: "Letra");
    }
}

internal sealed class SinteticaDropIndiceUnico : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropIndex(name: "UX_Atendimentos_FichaEmCurso", table: "Atendimentos", schema: PafDbContext.Schema);
}

internal sealed class SinteticaDropIndiceNaoUnico : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropIndex(name: "IX_Atendimentos_UnidadeData", table: "Atendimentos", schema: PafDbContext.Schema);
}

internal sealed class SinteticaDropIndiceDesconhecido : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropIndex(name: "IX_NaoExisteNoModelo", table: "Atendimentos", schema: PafDbContext.Schema);
}

internal sealed class SinteticaDropUniqueConstraint : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropUniqueConstraint(name: "AK_Estacoes_UnidadeId_Id", table: "Estacoes", schema: PafDbContext.Schema);
}

internal sealed class SinteticaRenameIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.RenameIndex(name: "UX_Atendimentos_FichaEmCurso", newName: "UX_Novo", table: "Atendimentos", schema: PafDbContext.Schema);
}

internal sealed class SinteticaSqlDropColumn : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""ALTER TABLE paf."Fichas" drop   column "Letra";""");
}

internal sealed class SinteticaSqlSetNotNull : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""ALTER TABLE paf."Atendimentos" ALTER COLUMN "EstacaoId" SET NOT NULL;""");
}

internal sealed class SinteticaSqlDropNotNull : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""ALTER TABLE paf."Fichas" ALTER COLUMN "Letra" DROP NOT NULL;""");
}

[MigracaoDeContracao("Fichas.Letra deixou de ser lida e gravada na versão anterior (exemplo do teste).")]
internal sealed class SinteticaContracaoMarcada : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "Letra", table: "Fichas", schema: PafDbContext.Schema);
        migrationBuilder.DropIndex(name: "UX_Atendimentos_FichaEmCurso", table: "Atendimentos", schema: PafDbContext.Schema);
    }
}

internal sealed class SinteticaDuasContracoes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "Observacao", table: "Fichas", schema: PafDbContext.Schema, type: "text", nullable: true);
        migrationBuilder.DropColumn(name: "Numero", table: "Fichas", schema: PafDbContext.Schema);
        migrationBuilder.RenameColumn(name: "Letra", table: "Fichas", newName: "Prefixo", schema: PafDbContext.Schema);
    }
}
