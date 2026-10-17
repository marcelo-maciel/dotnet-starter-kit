using Asp.Versioning;
using FSH.Framework.Persistence;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Web.Modules;
using FSH.Modules.PAF.Autorizacao;
using FSH.Modules.PAF.Conferencia;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Estacoes;
using FSH.Modules.PAF.Features.v1.Estacoes.GerarVinculo;
using FSH.Modules.PAF.Features.v1.Estacoes.ObterConfiguracao;
using FSH.Modules.PAF.Features.v1.Estacoes.RenovarCredencial;
using FSH.Modules.PAF.Features.v1.Estacoes.ResgatarVinculo;
using FSH.Modules.PAF.Features.v1.Estacoes.RevogarVinculo;
using Microsoft.AspNetCore.Authorization;
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

        RegistrarCredencialDeEstacao(builder.Services);
    }

    public void ConfigureMiddleware(IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<ConferenciaRequisicaoMiddleware>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var versionSet = endpoints.NewApiVersionSet()
            .HasApiVersion(new ApiVersion(1))
            .ReportApiVersions()
            .Build();

        var group = endpoints
            .MapGroup("api/v{version:apiVersion}")
            .WithTags("PAF")
            .WithApiVersionSet(versionSet);

        MapearEndpointsDeEstacao(group);
    }

    // Station credential (RF04, ADR 0008 "Token da estação"): permissions, the per-role policies and the issuer.
    private static void RegistrarCredencialDeEstacao(IServiceCollection services)
    {
        PermissionConstants.Register(PafPermissions.All);

        services.AddScoped<EmissorAcessoEstacao>();
        services.AddScoped<IAuthorizationHandler, EstacaoAuthorizationHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(PafPolicies.EstacaoEmissao, p => p
                .RequireAuthenticatedUser()
                .AddRequirements(new EstacaoRequirement(PafClaims.PapelEmissao)))
            .AddPolicy(PafPolicies.EstacaoPainel, p => p
                .RequireAuthenticatedUser()
                .AddRequirements(new EstacaoRequirement(PafClaims.PapelPainel)));
    }

    private static void MapearEndpointsDeEstacao(IEndpointRouteBuilder group)
    {
        // Literal "vinculo/resgatar" and "credencial/renovar" never collide with the "{estacaoId:guid}" routes.
        group.MapGenerateStationLinkEndpoint();
        group.MapRevokeStationLinkEndpoint();
        group.MapIssueStationCredentialEndpoint();
        group.MapRenewStationCredentialEndpoint();
        group.MapGetStationEmissionSettingsEndpoint();
        group.MapGetStationPanelSettingsEndpoint();
    }
}
