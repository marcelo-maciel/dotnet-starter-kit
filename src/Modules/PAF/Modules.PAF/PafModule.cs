using FSH.Framework.Eventing;
using FSH.Framework.Persistence;
using FSH.Framework.Web.Modules;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.TempoReal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

[assembly: FshModule(typeof(FSH.Modules.PAF.PafModule), 900)]

namespace FSH.Modules.PAF;

public sealed class PafModule : IModule
{
    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHeroDbContext<PafDbContext>();
        builder.Services.AddScoped<IDbInitializer, PafDbInitializer>();
        builder.Services.AddScoped<IUnidadeCorrente, UnidadeCorrente>();

        builder.Services.AddHealthChecks()
            .AddDbContextCheck<PafDbContext>(
                name: "db:paf",
                failureStatus: HealthStatus.Unhealthy);

        AddTempoReal(builder.Services);
    }

    public void ConfigureMiddleware(IApplicationBuilder app)
    {
        // No custom middleware needed
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        MapTempoReal(endpoints);
    }

    // --- Tempo real (seção 7) -------------------------------------------------------------------
    // Own methods so the parallel PAF branches touching this file merge line by line.

    private static void AddTempoReal(IServiceCollection services)
    {
        // SignalR and the Redis backplane come from the kit (AddHeroRealtime); PafHub shares them.
        services.AddAuthorizationBuilder()
            .AddPolicy(PafHub.Politica, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => ConexaoPaf.Ler(context.User) is not null));
        services.AddScoped<AvisosTempoReal>();
        services.AddIntegrationEventHandlers(typeof(PafModule).Assembly);
    }

    private static void MapTempoReal(IEndpointRouteBuilder endpoints)
    {
        // The kit enables this on no hub: a connection would outlive the token that opened it, and an
        // expired impersonation grant, which raises no event, would keep receiving the unit's events.
        endpoints.MapHub<PafHub>(PafHubRota.Caminho, options => options.CloseOnAuthenticationExpiration = true);
    }
}
