using Asp.Versioning;
using FSH.Framework.Persistence;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Web.Modules;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Contratos.CreateContratoUnidade;
using FSH.Modules.PAF.Features.v1.Contratos.ListContratosUnidade;
using FSH.Modules.PAF.Features.v1.Contratos.UpdateContratoUnidade;
using FSH.Modules.PAF.Features.v1.Estacoes.CreateEstacao;
using FSH.Modules.PAF.Features.v1.Estacoes.DisableEstacao;
using FSH.Modules.PAF.Features.v1.Estacoes.EnableEstacao;
using FSH.Modules.PAF.Features.v1.Estacoes.ListEstacoes;
using FSH.Modules.PAF.Features.v1.Estacoes.UpdateEstacao;
using FSH.Modules.PAF.Features.v1.Filas.AddFilaPrioridade;
using FSH.Modules.PAF.Features.v1.Filas.CreateFila;
using FSH.Modules.PAF.Features.v1.Filas.DisableFila;
using FSH.Modules.PAF.Features.v1.Filas.EnableFila;
using FSH.Modules.PAF.Features.v1.Filas.ListFilas;
using FSH.Modules.PAF.Features.v1.Filas.ListFilasConsolidadas;
using FSH.Modules.PAF.Features.v1.Filas.UpdateFila;
using FSH.Modules.PAF.Features.v1.Filas.UpdateFilaPrioridade;
using FSH.Modules.PAF.Features.v1.Prioridades.CreatePrioridade;
using FSH.Modules.PAF.Features.v1.Prioridades.DisablePrioridade;
using FSH.Modules.PAF.Features.v1.Prioridades.EnablePrioridade;
using FSH.Modules.PAF.Features.v1.Prioridades.ListPrioridades;
using FSH.Modules.PAF.Features.v1.Prioridades.UpdatePrioridade;
using FSH.Modules.PAF.Features.v1.Unidades.CreateUnidade;
using FSH.Modules.PAF.Features.v1.Unidades.DisableUnidade;
using FSH.Modules.PAF.Features.v1.Unidades.EnableUnidade;
using FSH.Modules.PAF.Features.v1.Unidades.ListUnidades;
using FSH.Modules.PAF.Features.v1.Unidades.UpdateUnidade;
using FSH.Modules.PAF.Features.v1.UsuariosUnidade.GetUsuarioUnidades;
using FSH.Modules.PAF.Features.v1.UsuariosUnidade.SetUsuarioUnidades;
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

        ConfigureCadastroServices(builder.Services);
    }

    public void ConfigureMiddleware(IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<UnidadeCorrenteMiddleware>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var versionSet = endpoints.NewApiVersionSet()
            .HasApiVersion(new ApiVersion(1))
            .ReportApiVersions()
            .Build();

        var group = endpoints
            .MapGroup("api/v{version:apiVersion}/paf")
            .WithTags("PAF")
            .WithApiVersionSet(versionSet)
            .RequireAuthorization();

        MapCadastroEndpoints(group);
    }

    // Cadastros and unit scope (feat/paf-cadastro).
    private static void ConfigureCadastroServices(IServiceCollection services)
    {
        PermissionConstants.Register(PafPermissions.All);
        services.AddScoped<EscopoUsuario>();
        services.AddScoped<PafDemoSeeder>();
    }

    // Cadastros and unit scope (feat/paf-cadastro).
    private static void MapCadastroEndpoints(IEndpointRouteBuilder group)
    {
        group.MapCreateUnidadeEndpoint();
        group.MapListUnidadesEndpoint();
        group.MapUpdateUnidadeEndpoint();
        group.MapEnableUnidadeEndpoint();
        group.MapDisableUnidadeEndpoint();

        group.MapCreatePrioridadeEndpoint();
        group.MapListPrioridadesEndpoint();
        group.MapUpdatePrioridadeEndpoint();
        group.MapEnablePrioridadeEndpoint();
        group.MapDisablePrioridadeEndpoint();

        group.MapCreateFilaEndpoint();
        group.MapListFilasEndpoint();
        group.MapListFilasConsolidadasEndpoint();
        group.MapUpdateFilaEndpoint();
        group.MapEnableFilaEndpoint();
        group.MapDisableFilaEndpoint();
        group.MapAddFilaPrioridadeEndpoint();
        group.MapUpdateFilaPrioridadeEndpoint();

        group.MapCreateEstacaoEndpoint();
        group.MapListEstacoesEndpoint();
        group.MapUpdateEstacaoEndpoint();
        group.MapEnableEstacaoEndpoint();
        group.MapDisableEstacaoEndpoint();

        group.MapCreateContratoUnidadeEndpoint();
        group.MapUpdateContratoUnidadeEndpoint();
        group.MapListContratosUnidadeEndpoint();

        group.MapGetUsuarioUnidadesEndpoint();
        group.MapSetUsuarioUnidadesEndpoint();
    }
}
