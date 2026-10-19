using System.Reflection;
using System.Text.RegularExpressions;
using FSH.Modules.PAF.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace PAF.Tests.Esquema;

/// <summary>
/// Expand and contract barrier (RNF16, O07): during a rolling deploy the previous API image, and the totem and painel
/// bundles that stay open for days, run against the NEW schema. So an expand migration may only add; anything the
/// previous image could still read or write (column, table, name, nullability, unique rule it maps to 409) is removed
/// only by a migration marked <see cref="MigracaoDeContracaoAttribute"/>.
/// </summary>
internal static partial class BarreiraExpandirContrair
{
    /// <summary>Every <c>PafDbContext</c> migration of the migrations assembly, oldest first.</summary>
    public static IReadOnlyList<Migration> MigracoesDoPaf(Assembly assemblyDasMigracoes)
    {
        return assemblyDasMigracoes.GetTypes()
            .Where(t => typeof(Migration).IsAssignableFrom(t) && !t.IsAbstract)
            .Where(t => t.GetCustomAttribute<DbContextAttribute>()?.ContextType == typeof(PafDbContext))
            .Where(t => t.GetCustomAttribute<MigrationAttribute>() is not null)
            .OrderBy(t => t.GetCustomAttribute<MigrationAttribute>()!.Id, StringComparer.Ordinal)
            .Select(t => (Migration)Activator.CreateInstance(t)!)
            .ToList();
    }

    public static string IdDe(Migration migracao) =>
        migracao.GetType().GetCustomAttribute<MigrationAttribute>()?.Id ?? migracao.GetType().Name;

    /// <summary>
    /// What <paramref name="migracao"/>'s Up does that the previous image cannot survive. <paramref name="modeloAnterior"/>
    /// is the target model of the migration before it: a DropIndex op carries only the name, and whether the dropped
    /// index was unique lives there. An index missing from it counts as unique (fail closed).
    /// </summary>
    public static IReadOnlyList<string> Violacoes(Migration migracao, IModel modeloAnterior)
    {
        if (migracao.GetType().GetCustomAttribute<MigracaoDeContracaoAttribute>() is not null)
        {
            return [];
        }

        var id = IdDe(migracao);
        return migracao.UpOperations
            .Select(op => Violacao(op, modeloAnterior))
            .OfType<string>()
            .Select(v => $"{id}: {v}")
            .ToList();
    }

    private static string? Violacao(MigrationOperation op, IModel modeloAnterior) => op switch
    {
        DropColumnOperation o => $"DropColumn {o.Table}.{o.Name}",
        DropTableOperation o => $"DropTable {o.Name}",
        DropSchemaOperation o => $"DropSchema {o.Name}",
        DropSequenceOperation o => $"DropSequence {o.Name}",
        RenameColumnOperation o => $"RenameColumn {o.Table}.{o.Name} -> {o.NewName}",
        RenameTableOperation o => $"RenameTable {o.Name} -> {o.NewName}",
        RenameIndexOperation o => $"RenameIndex {o.Name} -> {o.NewName}",
        RenameSequenceOperation o => $"RenameSequence {o.Name} -> {o.NewName}",
        AlterColumnOperation o when o.OldColumn.IsNullable && !o.IsNullable =>
            $"AlterColumn {o.Table}.{o.Name} de nulo para obrigatório",
        AddColumnOperation o when !o.IsNullable && !TemValorPadrao(o) =>
            $"AddColumn {o.Table}.{o.Name} obrigatória sem valor padrão (a imagem anterior insere sem ela)",
        DropIndexOperation o when IndiceEraUnico(modeloAnterior, o.Name) => $"DropIndex único {o.Name}",
        DropUniqueConstraintOperation o => $"DropUniqueConstraint {o.Name}",
        DropPrimaryKeyOperation o => $"DropPrimaryKey {o.Name}",
        SqlOperation o when SqlDeContracao().IsMatch(o.Sql) => $"Sql de contração: {o.Sql.Trim()}",
        _ => null,
    };

    private static bool TemValorPadrao(AddColumnOperation o) =>
        o.DefaultValue is not null || o.DefaultValueSql is not null || o.ComputedColumnSql is not null;

    private static bool IndiceEraUnico(IModel modeloAnterior, string nome)
    {
        var indice = modeloAnterior.GetEntityTypes()
            .SelectMany(e => e.GetIndexes())
            .FirstOrDefault(i => string.Equals(i.GetDatabaseName(), nome, StringComparison.Ordinal));
        return indice?.IsUnique ?? true;
    }

    // Raw SQL is how this repo writes what EF cannot model (deferred unique constraint, ADR 0005), so a hand-written
    // DROP / RENAME / SET NOT NULL would otherwise slip past the operation checks. DROP NOT NULL and DROP DEFAULT
    // only widen what the previous image may write, so they pass.
    [GeneratedRegex(@"\bDROP\s+(COLUMN|TABLE|CONSTRAINT|INDEX|SCHEMA|SEQUENCE)\b|\bRENAME\b|\bSET\s+NOT\s+NULL\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SqlDeContracao();
}
