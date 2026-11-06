using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace FSH.Modules.PAF.Observabilidade;

/// <summary>
/// RNF06: no personal data in clear text in the logs. The kit's <c>HttpRequestContextEnricher</c> writes the
/// caller's e-mail as <see cref="PropriedadeEmail"/> on every authenticated log event, with
/// <c>AddPropertyIfAbsent</c>; this enricher runs before it and claims the property first with
/// <see cref="Omitido"/>, so the kit's write becomes a no-op without editing the kit (ADR 0008).
/// </summary>
public sealed class EnriquecedorSemDadosPessoais : ILogEventEnricher
{
    public const string PropriedadeEmail = "UserEmail";
    public const string Omitido = "[omitido]";

    // HttpContextAccessor reads a static AsyncLocal, so a private instance sees the same request as the kit's.
    private readonly HttpContextAccessor _requisicao = new();

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        // Same condition as the kit's enricher: only where the kit would write the e-mail.
        if (_requisicao.HttpContext?.User?.Identity?.IsAuthenticated == true)
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(PropriedadeEmail, Omitido));
        }
    }
}

/// <summary>
/// Puts <see cref="EnriquecedorSemDadosPessoais"/> in the Serilog configuration the kit reads
/// (<c>logger.ReadFrom.Configuration</c>, applied before the kit's <c>Enrich.With(httpEnricher)</c>, and enrichers
/// run in registration order). Registered by the module itself, not by the environment files, so no environment
/// and no test host can run without it.
/// </summary>
public static class LogSemDadosPessoais
{
    // Index far past the kit's arrays (Using has 1-2 entries, Enrich 6): environment entries and kit updates
    // replace the arrays by index and would otherwise collide with these.
    public const string ChaveUsing = "Serilog:Using:90";
    public const string ChaveEnrich = "Serilog:Enrich:90";
    public const string NomeDoMetodo = nameof(WithPafSemDadosPessoais);

    public static LoggerConfiguration WithPafSemDadosPessoais(this LoggerEnrichmentConfiguration enrich)
    {
        ArgumentNullException.ThrowIfNull(enrich);
        return enrich.With(new EnriquecedorSemDadosPessoais());
    }

    internal static void Registrar(IConfigurationBuilder configuracao) =>
        configuracao.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ChaveUsing] = typeof(LogSemDadosPessoais).Assembly.GetName().Name,
            [ChaveEnrich] = NomeDoMetodo,
        });
}
