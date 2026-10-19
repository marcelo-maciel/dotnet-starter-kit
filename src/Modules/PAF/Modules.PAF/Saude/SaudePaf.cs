using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Sessao;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace FSH.Modules.PAF.Saude;

/// <summary>
/// Saúde própria do PAF (seção 9 da arquitetura, RNF05): o kit responde 503 em <c>/health/ready</c> sem Valkey, mas
/// sem Valkey o totem e o guichê seguem no modo degradado. Aqui o Valkey fora é <c>Degraded</c> (200) e só o
/// PostgreSQL fora é <c>Unhealthy</c> (503), que é o que o balanceador e o deploy precisam distinguir.
/// </summary>
public static class SaudePaf
{
    public const string Rota = "/saude";
    public const string Etiqueta = "paf";
    public const string NomePostgres = "db:paf";
    public const string NomeValkey = "valkey:paf";

    /// <summary>Teto da conferência do PostgreSQL: a sonda responde 503 antes de 3 s com o banco parado.</summary>
    public static readonly TimeSpan PrazoPostgres = TimeSpan.FromSeconds(2);

    internal static RouteHandlerBuilder MapSaudeEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(Rota,
                async (HealthCheckService saude, CancellationToken ct) =>
                {
                    var relatorio = await saude.CheckHealthAsync(r => r.Tags.Contains(Etiqueta), ct);
                    // Anonymous route: name, status and our own fixed description only, never the exception.
                    var corpo = new SaudePafDto(
                        relatorio.Status.ToString(),
                        relatorio.Entries.Select(e => new ItemSaudePafDto(e.Key, e.Value.Status.ToString(), e.Value.Description)).ToList());
                    var status = relatorio.Status == HealthStatus.Unhealthy
                        ? StatusCodes.Status503ServiceUnavailable
                        : StatusCodes.Status200OK;
                    return Results.Json(corpo, statusCode: status);
                })
            .WithName("PafSaude")
            .WithSummary("PAF health: Healthy, Degraded (Valkey down, degraded mode) or 503 without PostgreSQL")
            .AllowAnonymous()
            .DisableRateLimiting()
            .Produces<SaudePafDto>(StatusCodes.Status200OK)
            .Produces<SaudePafDto>(StatusCodes.Status503ServiceUnavailable);
}

public sealed record SaudePafDto(string Status, IReadOnlyList<ItemSaudePafDto> Itens);

public sealed record ItemSaudePafDto(string Nome, string Status, string? Descricao);

/// <summary>PostgreSQL do PAF, com teto próprio: um banco parado não pode segurar a sonda.</summary>
public sealed class SaudePostgresPaf(PafDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // WaitAsync, not only the token: a paused server can keep the driver past a cancellation. The
        // TimeoutException reaches the HealthCheckService, which reports the registration's failure status.
        var conecta = await db.Database.CanConnectAsync(cancellationToken).WaitAsync(SaudePaf.PrazoPostgres, cancellationToken);
        return conecta
            ? HealthCheckResult.Healthy("PostgreSQL acessível.")
            : HealthCheckResult.Unhealthy("PostgreSQL inacessível.");
    }
}

/// <summary>
/// Valkey do PAF lido pelo conjunto de licenças (prazo de 500 ms por chamada): fora, ou de volta mas ainda sem a
/// reconstrução do conjunto, é o modo degradado.
/// </summary>
public sealed class SaudeValkeyPaf(IEnumerable<IConnectionMultiplexer> multiplexers, ConjuntoLicencas conjunto) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // A host without Valkey configured (single instance, the shared test host) is not degraded by an outage:
        // there is nothing to come back. Production always configures it (O02 checks the environment files).
        if (!multiplexers.Any())
        {
            return HealthCheckResult.Healthy("Valkey não configurado.");
        }

        return await conjunto.ReconstruidoAsync(cancellationToken) switch
        {
            true => HealthCheckResult.Healthy("Valkey acessível e conjunto de licenças reconstruído."),
            false => HealthCheckResult.Degraded("Valkey de volta; conjunto de licenças em reconstrução (modo degradado)."),
            null => HealthCheckResult.Degraded("Valkey indisponível (modo degradado)."),
        };
    }
}
