using FSH.Modules.PAF.TempoReal;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace FSH.Modules.PAF.Observabilidade;

/// <summary>
/// A refusal by <see cref="PafHubFilter"/> is an expected outcome: <see cref="PafHubFilter.LicencaRecusadaException"/>
/// (licence refused on re-occupation) and <see cref="PafHubFilter.CredencialRecusadaException"/> (credential revoked,
/// session ended) are thrown on purpose, because the exception's message is the cause the screen shows, and the filter
/// logs each refusal itself (events 9006 and 9003). SignalR logs every exception out of OnConnectedAsync
/// (HubConnectionHandler) and out of a hub method (DefaultHubDispatcher) as an Error; this filter drops only those
/// duplicates, so any other hub error still reaches the sinks.
/// </summary>
public sealed class FiltroSemErroDeRecusaDoHub : ILogEventFilter
{
    public const string AberturaDoSignalR = "Microsoft.AspNetCore.SignalR.HubConnectionHandler";
    public const string ChamadaDoSignalR = "Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher";

    public bool IsEnabled(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        return logEvent.Exception is not (PafHubFilter.LicencaRecusadaException or PafHubFilter.CredencialRecusadaException)
            || !logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var fonte)
            || fonte is not ScalarValue { Value: AberturaDoSignalR or ChamadaDoSignalR };
    }
}

/// <summary>
/// Puts <see cref="FiltroSemErroDeRecusaDoHub"/> in the Serilog configuration the kit reads
/// (<c>logger.ReadFrom.Configuration</c>), without editing the kit (ADR 0008). The assembly is already in
/// <c>Serilog:Using</c> through <see cref="LogSemDadosPessoais"/>, which is how Serilog finds this method.
/// </summary>
public static class LogSemErroDeRecusaDoHub
{
    // Same far index as LogSemDadosPessoais: environment entries and kit updates replace the arrays by index.
    public const string ChaveFilter = "Serilog:Filter:90";
    public const string NomeDoMetodo = nameof(WithPafSemErroDeRecusaDoHub);

    public static LoggerConfiguration WithPafSemErroDeRecusaDoHub(this LoggerFilterConfiguration filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return filter.With(new FiltroSemErroDeRecusaDoHub());
    }

    internal static void Registrar(IConfigurationBuilder configuracao) =>
        configuracao.AddInMemoryCollection(new Dictionary<string, string?> { [ChaveFilter] = NomeDoMetodo });
}
