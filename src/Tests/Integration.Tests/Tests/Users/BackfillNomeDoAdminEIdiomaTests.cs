using FSH.Starter.Migrations.PostgreSQL.Identity;
using Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Integration.Tests.Tests.Users;

/// <summary>
/// The data migration that renames the old seeded admins and fills the missing locale, run against the real
/// PostgreSQL. The SQL comes from the migration's own operations, so the test exercises the shipped text.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class BackfillNomeDoAdminEIdiomaTests(FshWebApplicationFactory factory)
{
    [Fact]
    public async Task Migracao_AdminComNomeAntigoELocaleNulo_RenomeiaEGravaPtBr()
    {
        var sufixo = Guid.NewGuid().ToString("N")[..8];
        var tenantId = $"bk{sufixo}";
        await using var conexao = await AbrirAsync();
        await InserirTenantAsync(conexao, tenantId, nome: $"Loja {sufixo}");
        var admin = await InserirUsuarioAsync(conexao, tenantId, $"{tenantId}.Admin".ToUpperInvariant(), tenantId.ToUpperInvariant(), "Admin", locale: null);

        await ExecutarMigracaoAsync(conexao);

        var (primeiro, ultimo, locale) = await LerAsync(conexao, admin);
        primeiro.ShouldBe("Administrador");
        ultimo.ShouldBe($"Loja {sufixo}");
        locale.ShouldBe("pt-BR");
    }

    [Fact]
    public async Task Migracao_TenantSemNome_UsaOIdComoSobrenome()
    {
        var sufixo = Guid.NewGuid().ToString("N")[..8];
        var tenantId = $"bk{sufixo}";
        await using var conexao = await AbrirAsync();
        await InserirTenantAsync(conexao, tenantId, nome: null);
        var admin = await InserirUsuarioAsync(conexao, tenantId, $"{tenantId}.Admin".ToUpperInvariant(), tenantId.ToUpperInvariant(), "Admin", locale: null);

        await ExecutarMigracaoAsync(conexao);

        var (primeiro, ultimo, _) = await LerAsync(conexao, admin);
        primeiro.ShouldBe("Administrador");
        ultimo.ShouldBe(tenantId);
    }

    [Fact]
    public async Task Migracao_AdminEditadoPorAlguem_MantemONomeEPreencheSoOLocale()
    {
        var sufixo = Guid.NewGuid().ToString("N")[..8];
        var tenantId = $"bk{sufixo}";
        await using var conexao = await AbrirAsync();
        await InserirTenantAsync(conexao, tenantId, nome: $"Loja {sufixo}");
        var admin = await InserirUsuarioAsync(conexao, tenantId, $"{tenantId}.Admin".ToUpperInvariant(), "Maria", "Silva", locale: null);

        await ExecutarMigracaoAsync(conexao);

        var (primeiro, ultimo, locale) = await LerAsync(conexao, admin);
        primeiro.ShouldBe("Maria");
        ultimo.ShouldBe("Silva");
        locale.ShouldBe("pt-BR");
    }

    [Fact]
    public async Task Migracao_UsuarioComLocaleDefinido_MantemOLocale()
    {
        var sufixo = Guid.NewGuid().ToString("N")[..8];
        var tenantId = $"bk{sufixo}";
        await using var conexao = await AbrirAsync();
        await InserirTenantAsync(conexao, tenantId, nome: $"Loja {sufixo}");
        var usuario = await InserirUsuarioAsync(conexao, tenantId, $"JOAO{sufixo}".ToUpperInvariant(), "João", "Souza", locale: "en-US");

        await ExecutarMigracaoAsync(conexao);

        var (_, _, locale) = await LerAsync(conexao, usuario);
        locale.ShouldBe("en-US");
    }

    [Fact]
    public async Task Migracao_ExecutadaDuasVezes_NaoAlteraNadaAlemDaPrimeira()
    {
        var sufixo = Guid.NewGuid().ToString("N")[..8];
        var tenantId = $"bk{sufixo}";
        await using var conexao = await AbrirAsync();
        await InserirTenantAsync(conexao, tenantId, nome: $"Loja {sufixo}");
        var admin = await InserirUsuarioAsync(conexao, tenantId, $"{tenantId}.Admin".ToUpperInvariant(), tenantId.ToUpperInvariant(), "Admin", locale: null);

        await ExecutarMigracaoAsync(conexao);
        var aposPrimeira = await LerAsync(conexao, admin);
        await ExecutarMigracaoAsync(conexao);

        (await LerAsync(conexao, admin)).ShouldBe(aposPrimeira);
    }

    [Fact]
    public async Task Migracao_BancoDedicadoSemCatalogoDeTenants_PulaORenomeEGravaOLocale()
    {
        // A tenant with its own connection string gets the Identity migrations in a database that has no tenant schema.
        await using (var compartilhado = await AbrirAsync())
        {
            // Separate commands: a multi-statement batch is an implicit transaction, which CREATE DATABASE refuses.
            await using var apagar = new NpgsqlCommand("DROP DATABASE IF EXISTS bk_banco_dedicado WITH (FORCE)", compartilhado);
            await apagar.ExecuteNonQueryAsync();
            await using var criar = new NpgsqlCommand("CREATE DATABASE bk_banco_dedicado", compartilhado);
            await criar.ExecuteNonQueryAsync();
        }

        var dedicado = new NpgsqlConnectionStringBuilder(factory.Services.GetRequiredService<IConfiguration>()["DatabaseOptions:ConnectionString"])
        {
            Database = "bk_banco_dedicado",
        };
        await using var conexao = new NpgsqlConnection(dedicado.ConnectionString);
        await conexao.OpenAsync();
        await using (var preparar = new NpgsqlBatch(conexao)
        {
            BatchCommands =
            {
                new NpgsqlBatchCommand("CREATE SCHEMA identity"),
                new NpgsqlBatchCommand(
                    """CREATE TABLE identity."Users" ("Id" text PRIMARY KEY, "FirstName" text, "LastName" text, "Locale" varchar(10), "TenantId" text, "NormalizedUserName" text)"""),
                new NpgsqlBatchCommand(
                    """INSERT INTO identity."Users" VALUES ('u1', 'DEDICADO', 'Admin', NULL, 'dedicado', 'DEDICADO.ADMIN')"""),
            },
        })
        {
            await preparar.ExecuteNonQueryAsync();
        }

        await ExecutarMigracaoAsync(conexao);

        var (primeiro, ultimo, locale) = await LerAsync(conexao, "u1");
        primeiro.ShouldBe("DEDICADO");
        ultimo.ShouldBe("Admin");
        locale.ShouldBe("pt-BR");
    }

    private async Task<NpgsqlConnection> AbrirAsync()
    {
        var conexao = new NpgsqlConnection(factory.Services.GetRequiredService<IConfiguration>()["DatabaseOptions:ConnectionString"]);
        await conexao.OpenAsync();
        return conexao;
    }

    private static async Task ExecutarMigracaoAsync(NpgsqlConnection conexao)
    {
        // One batch command per migration statement (each SqlOperation is a single statement): NpgsqlCommand with a
        // non-constant text trips CA2100.
        await using var lote = new NpgsqlBatch(conexao);
        foreach (var operacao in new BackfillNomeDoAdminEIdioma().UpOperations.OfType<SqlOperation>())
        {
            lote.BatchCommands.Add(new NpgsqlBatchCommand(operacao.Sql));
        }

        lote.BatchCommands.Count.ShouldBe(2);
        await lote.ExecuteNonQueryAsync();
    }

    private static async Task InserirTenantAsync(NpgsqlConnection conexao, string id, string? nome)
    {
        await using var comando = new NpgsqlCommand(
            """
            INSERT INTO tenant."Tenants" ("Id", "Identifier", "Name", "ConnectionString", "AdminEmail", "IsActive", "ValidUpto", "QuotaLimits")
            VALUES (@id, @id, @nome, '', @email, true, now() + interval '1 year', '{}')
            """,
            conexao);
        comando.Parameters.AddWithValue("id", id);
        comando.Parameters.AddWithValue("nome", (object?)nome ?? DBNull.Value);
        comando.Parameters.AddWithValue("email", $"{id}@teste.com");
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task<string> InserirUsuarioAsync(
        NpgsqlConnection conexao, string tenantId, string nomeNormalizado, string primeiroNome, string sobrenome, string? locale)
    {
        var id = Guid.NewGuid().ToString();
        await using var comando = new NpgsqlCommand(
            """
            INSERT INTO identity."Users" ("Id", "FirstName", "LastName", "Locale", "TenantId", "UserName", "NormalizedUserName",
                "IsActive", "RefreshTokenExpiryTime", "LastPasswordChangeDate", "EmailConfirmed", "PhoneNumberConfirmed",
                "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            VALUES (@id, @primeiro, @ultimo, @locale, @tenant, @usuario, @normalizado,
                true, now(), now(), true, false, false, false, 0)
            """,
            conexao);
        comando.Parameters.AddWithValue("id", id);
        comando.Parameters.AddWithValue("primeiro", primeiroNome);
        comando.Parameters.AddWithValue("ultimo", sobrenome);
        comando.Parameters.AddWithValue("locale", (object?)locale ?? DBNull.Value);
        comando.Parameters.AddWithValue("tenant", tenantId);
        comando.Parameters.AddWithValue("usuario", nomeNormalizado);
        comando.Parameters.AddWithValue("normalizado", nomeNormalizado);
        await comando.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task<(string? Primeiro, string? Ultimo, string? Locale)> LerAsync(NpgsqlConnection conexao, string id)
    {
        await using var comando = new NpgsqlCommand(
            "SELECT \"FirstName\", \"LastName\", \"Locale\" FROM identity.\"Users\" WHERE \"Id\" = @id", conexao);
        comando.Parameters.AddWithValue("id", id);
        await using var leitor = await comando.ExecuteReaderAsync();
        (await leitor.ReadAsync()).ShouldBeTrue();
        return (
            await leitor.IsDBNullAsync(0) ? null : leitor.GetString(0),
            await leitor.IsDBNullAsync(1) ? null : leitor.GetString(1),
            await leitor.IsDBNullAsync(2) ? null : leitor.GetString(2));
    }
}
