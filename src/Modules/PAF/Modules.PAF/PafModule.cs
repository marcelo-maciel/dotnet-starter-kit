using Asp.Versioning;
using FSH.Framework.Eventing;
using FSH.Framework.Persistence;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Web.Modules;
using FSH.Modules.Identity.Contracts.DTOs;
using FSH.Modules.Identity.Contracts.v1.Tokens.RefreshToken;
using FSH.Modules.Identity.Contracts.v1.Tokens.TokenGeneration;
using FSH.Modules.PAF.Autorizacao;
using FSH.Modules.PAF.Conferencia;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Estacoes;
using FSH.Modules.PAF.Features.v1.Atendimentos.CancelarAtendimento;
using FSH.Modules.PAF.Features.v1.Atendimentos.ChamarProximaFicha;
using FSH.Modules.PAF.Features.v1.Atendimentos.EncerrarAtendimento;
using FSH.Modules.PAF.Features.v1.Atendimentos.IniciarAtendimento;
using FSH.Modules.PAF.Features.v1.Atendimentos.RechamarAtendimento;
using FSH.Modules.PAF.Features.v1.Contratos.CreateContratoUnidade;
using FSH.Modules.PAF.Features.v1.Contratos.ListContratosUnidade;
using FSH.Modules.PAF.Features.v1.Contratos.UpdateContratoUnidade;
using FSH.Modules.PAF.Features.v1.Estacoes.CreateEstacao;
using FSH.Modules.PAF.Features.v1.Estacoes.DisableEstacao;
using FSH.Modules.PAF.Features.v1.Estacoes.EnableEstacao;
using FSH.Modules.PAF.Features.v1.Estacoes.GerarVinculo;
using FSH.Modules.PAF.Features.v1.Estacoes.ListEstacoes;
using FSH.Modules.PAF.Features.v1.Estacoes.ObterConfiguracao;
using FSH.Modules.PAF.Features.v1.Estacoes.RenovarCredencial;
using FSH.Modules.PAF.Features.v1.Estacoes.ResgatarVinculo;
using FSH.Modules.PAF.Features.v1.Estacoes.RevogarVinculo;
using FSH.Modules.PAF.Features.v1.Estacoes.UpdateEstacao;
using FSH.Modules.PAF.Features.v1.Fichas.EmitirFicha;
using FSH.Modules.PAF.Features.v1.Filas.AddFilaPrioridade;
using FSH.Modules.PAF.Features.v1.Filas.CreateFila;
using FSH.Modules.PAF.Features.v1.Filas.DisableFila;
using FSH.Modules.PAF.Features.v1.Filas.EnableFila;
using FSH.Modules.PAF.Features.v1.Filas.ListFilas;
using FSH.Modules.PAF.Features.v1.Filas.ListFilasConsolidadas;
using FSH.Modules.PAF.Features.v1.Filas.UpdateFila;
using FSH.Modules.PAF.Features.v1.Filas.UpdateFilaPrioridade;
using FSH.Modules.PAF.Features.v1.Painel.ObterEstadoPainel;
using FSH.Modules.PAF.Features.v1.Prioridades.CreatePrioridade;
using FSH.Modules.PAF.Features.v1.Prioridades.DisablePrioridade;
using FSH.Modules.PAF.Features.v1.Prioridades.EnablePrioridade;
using FSH.Modules.PAF.Features.v1.Prioridades.ListPrioridades;
using FSH.Modules.PAF.Features.v1.Prioridades.UpdatePrioridade;
using FSH.Modules.PAF.Features.v1.Sessao.EndSessao;
using FSH.Modules.PAF.Features.v1.Sessao.SetGuiche;
using FSH.Modules.PAF.Features.v1.Sessao.SetUnidadeCorrente;
using FSH.Modules.PAF.Features.v1.Unidades.CreateUnidade;
using FSH.Modules.PAF.Features.v1.Unidades.DisableUnidade;
using FSH.Modules.PAF.Features.v1.Unidades.EnableUnidade;
using FSH.Modules.PAF.Features.v1.Unidades.ListUnidades;
using FSH.Modules.PAF.Features.v1.Unidades.UpdateUnidade;
using FSH.Modules.PAF.Features.v1.UsuariosUnidade.GetUsuarioUnidades;
using FSH.Modules.PAF.Features.v1.UsuariosUnidade.SetUsuarioUnidades;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
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

        PermissionConstants.Register(PafPermissions.All);
        ConfigureCadastroServices(builder.Services);
        RegistrarCredencialDeEstacao(builder.Services);
        AddTempoReal(builder.Services);
        ConfigurarSessao(builder.Services);
    }

    public void ConfigureMiddleware(IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Order decides who sets the current unit: the station credential (Conferencia), then the person's
        // session (SessionCheck); the unit header only fills what neither of them set.
        app.UseMiddleware<ConferenciaRequisicaoMiddleware>();
        app.UseMiddleware<SessionCheckMiddleware>();
        app.UseMiddleware<UnidadeCorrenteMiddleware>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var versionSet = endpoints.NewApiVersionSet()
            .HasApiVersion(new ApiVersion(1))
            .ReportApiVersions()
            .Build();

        var cadastro = endpoints
            .MapGroup("api/v{version:apiVersion}/paf")
            .WithTags("PAF")
            .WithApiVersionSet(versionSet)
            .RequireAuthorization();
        MapCadastroEndpoints(cadastro);

        // No group-wide RequireAuthorization: redeeming a link and renewing a credential are anonymous.
        var estacao = endpoints
            .MapGroup("api/v{version:apiVersion}")
            .WithTags("PAF")
            .WithApiVersionSet(versionSet);
        MapearEndpointsDeEstacao(estacao);

        var sessao = endpoints
            .MapGroup("api/v{version:apiVersion}/paf/sessao")
            .WithTags("PAF Sessão")
            .WithApiVersionSet(versionSet)
            .RequireAuthorization();
        MapearSessao(sessao);

        // Person operations on the guichê selected by the session (RF15, RF16, RF21, seção 8).
        var guiche = endpoints
            .MapGroup("api/v{version:apiVersion}/paf/guiche")
            .WithTags("PAF Guichê")
            .WithApiVersionSet(versionSet)
            .RequireAuthorization();
        MapearGuiche(guiche);

        MapTempoReal(endpoints);
    }

    // Licença, sessão única, unidade corrente e guichê (RF22, ADR 0006, ADR 0008).
    private static void ConfigurarSessao(IServiceCollection services)
    {
        services.AddScoped<SessaoCorrente>();
        services.AddScoped<Licencas>();
        services.AddScoped<EstadoSessao>();
        services.AddScoped<IPipelineBehavior<GenerateTokenCommand, TokenResponse>, GenerateTokenLicenseBehavior>();
        services.AddScoped<IPipelineBehavior<RefreshTokenCommand, RefreshTokenCommandResponse>, RefreshTokenLicenseBehavior>();
    }

    private static void MapearSessao(IEndpointRouteBuilder sessao)
    {
        sessao.MapSetUnidadeCorrenteEndpoint();
        sessao.MapSetGuicheEndpoint();
        sessao.MapEndSessaoEndpoint();
    }

    private static void MapearGuiche(IEndpointRouteBuilder guiche)
    {
        guiche.MapRequestNextFichaEndpoint();
        guiche.MapResendCallEndpoint();
        guiche.MapStartAtendimentoEndpoint();
        guiche.MapEndAtendimentoEndpoint();
        guiche.MapVoidAtendimentoEndpoint();
    }

    // Cadastros and unit scope.
    private static void ConfigureCadastroServices(IServiceCollection services)
    {
        services.AddScoped<EscopoUsuario>();
        services.AddScoped<PafDemoSeeder>();
    }

    // Cadastros and unit scope.
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

    // Station credential (RF04, ADR 0008 "Token da estação"): the per-role policies and the issuer.
    private static void RegistrarCredencialDeEstacao(IServiceCollection services)
    {
        services.AddScoped<EmissorAcessoEstacao>();
        services.AddScoped<IAuthorizationHandler, EstacaoAuthorizationHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(PafPolicies.EstacaoEmissao, p => p
                .RequireAuthenticatedUser()
                .AddRequirements(new EstacaoRequirement(PafPapeisEstacao.Emissao)))
            .AddPolicy(PafPolicies.EstacaoPainel, p => p
                .RequireAuthenticatedUser()
                .AddRequirements(new EstacaoRequirement(PafPapeisEstacao.Painel)));
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
        group.MapIssueFichaEndpoint();
        group.MapGetPanelStateEndpoint();
    }

    // --- Tempo real (seção 7) -------------------------------------------------------------------

    private static void AddTempoReal(IServiceCollection services)
    {
        // SignalR and the Redis backplane come from the kit (AddHeroRealtime); PafHub shares them.
        services.AddAuthorizationBuilder()
            .AddPolicy(PafHub.Politica, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => ConexaoPaf.Ler(context.User) is not null));
        services.AddScoped<AvisosTempoReal>();
        services.AddSingleton<ConexoesPaf>();
        services.AddSingleton<PafHubFilter>();
        services.Configure<HubOptions<PafHub>>(options => options.AddFilter<PafHubFilter>());
        services.AddIntegrationEventHandlers(typeof(PafModule).Assembly);
    }

    private static void MapTempoReal(IEndpointRouteBuilder endpoints)
    {
        // The kit enables this on no hub: a connection would outlive the token that opened it, and an
        // expired impersonation grant, which raises no event, would keep receiving the unit's events.
        endpoints.MapHub<PafHub>(PafHubRota.Caminho, options => options.CloseOnAuthenticationExpiration = true);
    }
}
