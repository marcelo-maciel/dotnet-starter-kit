using FSH.Modules.PAF.TempoReal;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace FSH.Modules.PAF.Observabilidade;

/// <summary>
/// A licence refused when a hub connection opens is an expected outcome: <see cref="PafHubFilter"/> throws
/// <see cref="PafHubFilter.LicencaRecusadaException"/> on purpose, because the exception's message is the close reason
/// the screen shows, and it logs the refusal itself (event 9006). SignalR logs every exception out of OnConnectedAsync
/// as an Error; this filter drops only that duplicate, so any other hub error still reaches the sinks.
/// </summary>
public sealed class FiltroSemErroDeLicencaRecusada : ILogEventFilter
{
    public const string FonteDoSignalR = "Microsoft.AspNetCore.SignalR.HubConnectionHandler";

    public bool IsEnabled(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        return logEvent.Exception is not PafHubFilter.LicencaRecusadaException
            || !logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var fonte)
            || fonte is not ScalarValue { Value: FonteDoSignalR };
    }
}

/// <summary>
/// Puts <see cref="FiltroSemErroDeLicencaRecusada"/> in the Serilog configuration the kit reads
/// (<c>logger.ReadFrom.Configuration</c>), without editing the kit (ADR 0008). The assembly is already in
/// <c>Serilog:Using</c> through <see cref="LogSemDadosPessoais"/>, which is how Serilog finds this method.
/// </summary>
public static class LogSemErroDeLicencaRecusada
{
    // Same far index as LogSemDadosPessoais: environment entries and kit updates replace the arrays by index.
    public const string ChaveFilter = "Serilog:Filter:90";
    public const string NomeDoMetodo = nameof(WithPafSemErroDeLicencaRecusada);

    public static LoggerConfiguration WithPafSemErroDeLicencaRecusada(this LoggerFilterConfiguration filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return filter.With(new FiltroSemErroDeLicencaRecusada());
    }

    internal static void Registrar(IConfigurationBuilder configuracao) =>
        configuracao.AddInMemoryCollection(new Dictionary<string, string?> { [ChaveFilter] = NomeDoMetodo });
}
