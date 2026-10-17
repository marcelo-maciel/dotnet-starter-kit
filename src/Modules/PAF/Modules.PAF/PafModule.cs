using Asp.Versioning;
using FSH.Framework.Persistence;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Web.Modules;
using FSH.Modules.Identity.Contracts.DTOs;
using FSH.Modules.Identity.Contracts.v1.Tokens.RefreshToken;
using FSH.Modules.Identity.Contracts.v1.Tokens.TokenGeneration;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Sessao.EndSessao;
using FSH.Modules.PAF.Features.v1.Sessao.SetGuiche;
using FSH.Modules.PAF.Features.v1.Sessao.SetUnidadeCorrente;
using FSH.Modules.PAF.Sessao;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

        ConfigurarSessao(builder.Services);
    }

    // Licença, sessão única, unidade corrente e guichê (RF22, ADR 0006, ADR 0008).
    private static void ConfigurarSessao(IServiceCollection services)
    {
        PermissionConstants.Register(PafSessaoPermissions.All);
        services.AddScoped<SessaoCorrente>();
        services.AddScoped<Licencas>();
        services.AddScoped<EstadoSessao>();
        services.AddScoped<IPipelineBehavior<GenerateTokenCommand, TokenResponse>, GenerateTokenLicenseBehavior>();
        services.AddScoped<IPipelineBehavior<RefreshTokenCommand, RefreshTokenCommandResponse>, RefreshTokenLicenseBehavior>();
    }

    public void ConfigureMiddleware(IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseMiddleware<SessionCheckMiddleware>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        MapearSessao(endpoints);
    }

    private static void MapearSessao(IEndpointRouteBuilder endpoints)
    {
        var versionSet = endpoints.NewApiVersionSet()
            .HasApiVersion(new ApiVersion(1))
            .ReportApiVersions()
            .Build();

        var sessao = endpoints
            .MapGroup("api/v{version:apiVersion}/paf/sessao")
            .WithTags("PAF Sessão")
            .WithApiVersionSet(versionSet)
            .RequireAuthorization();

        sessao.MapSetUnidadeCorrenteEndpoint();
        sessao.MapSetGuicheEndpoint();
        sessao.MapEndSessaoEndpoint();
    }
}
