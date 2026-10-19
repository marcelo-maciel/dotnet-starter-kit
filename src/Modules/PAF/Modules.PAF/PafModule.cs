using Asp.Versioning;
using FSH.Framework.Eventing;
using FSH.Framework.Persistence;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Web.Modules;
using Hangfire;
using Hangfire.Common;
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
using FSH.Modules.PAF.Features.v1.Atendimentos.ChamarFichaEscolhida;
using FSH.Modules.PAF.Features.v1.Atendimentos.ChamarFichaVip;
using FSH.Modules.PAF.Features.v1.Atendimentos.ChamarProximaFicha;
using FSH.Modules.PAF.Features.v1.Atendimentos.EncerrarAtendimento;
using FSH.Modules.PAF.Features.v1.Atendimentos.EncaminharAtendimento;
using FSH.Modules.PAF.Features.v1.Atendimentos.IniciarAtendimento;
using FSH.Modules.PAF.Features.v1.Atendimentos.ListDestinosEncaminhamento;
using FSH.Modules.PAF.Features.v1.Atendimentos.ListFichasEmEspera;
using FSH.Modules.PAF.Features.v1.Atendimentos.RechamarAtendimento;
using FSH.Modules.PAF.Features.v1.Atendimentos.ReabilitarAtendimentoDaFicha;
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
using FSH.Modules.PAF.Features.v1.Fichas.CancelarAtendimentoDaFicha;
using FSH.Modules.PAF.Features.v1.Fichas.ConsultarFicha;
using FSH.Modules.PAF.Features.v1.Fichas.EmitirFicha;
using FSH.Modules.PAF.Features.v1.Fichas.ListAtendimentosDoDia;
using FSH.Modules.PAF.Features.v1.Fichas.ObterImpressaoFicha;
using FSH.Modules.PAF.Features.v1.Fichas.ListFichasEmAtendimento;
using FSH.Modules.PAF.Features.v1.Sessao.GetAvisoLicenca;
using FSH.Modules.PAF.Features.v1.Filas.AddFilaPrioridade;
using FSH.Modules.PAF.Features.v1.Guiches.LiberarGuiche;
using FSH.Modules.PAF.Features.v1.Guiches.ListGuichesDaUnidade;
using FSH.Modules.PAF.Features.v1.Guiches.ListMinhasUnidades;
using FSH.Modules.PAF.Features.v1.Guiches.ObterPosto;
using FSH.Modules.PAF.Features.v1.Filas.CreateFila;
using FSH.Modules.PAF.Features.v1.Filas.DisableFila;
using FSH.Modules.PAF.Features.v1.Filas.EnableFila;
using FSH.Modules.PAF.Features.v1.Filas.ListFilas;
using FSH.Modules.PAF.Features.v1.Estatistica;
using FSH.Modules.PAF.Features.v1.Estatistica.Atendimentos;
using FSH.Modules.PAF.Features.v1.Estatistica.ConsultarEstatistica;
using FSH.Modules.PAF.Features.v1.Filas.ListFilasConsolidadas;
using FSH.Modules.PAF.Features.v1.Filas.UpdateFila;
using FSH.Modules.PAF.Features.v1.Filas.UpdateFilaPrioridade;
using FSH.Modules.PAF.Features.v1.Setores.CreateSetor;
using FSH.Modules.PAF.Features.v1.Setores.ListSetores;
using FSH.Modules.PAF.Features.v1.Setores.UpdateSetor;
using FSH.Modules.PAF.Features.v1.Painel.ObterEstadoPainel;
using FSH.Modules.PAF.Features.v1.Prioridades.CreatePrioridade;
using FSH.Modules.PAF.Features.v1.Prioridades.DisablePrioridade;
using FSH.Modules.PAF.Features.v1.Prioridades.EnablePrioridade;
using FSH.Modules.PAF.Features.v1.Parametros.ListParametros;
using FSH.Modules.PAF.Features.v1.Parametros.ObterTeclasGuiche;
using FSH.Modules.PAF.Features.v1.Parametros.RemoveParametroUnidade;
using FSH.Modules.PAF.Features.v1.Parametros.SetParametroEmpresa;
using FSH.Modules.PAF.Features.v1.Parametros.SetParametroUnidade;
using FSH.Modules.PAF.Features.v1.Prioridades.ListPrioridades;
using FSH.Modules.PAF.Features.v1.Prioridades.UpdatePrioridade;
using FSH.Modules.PAF.Features.v1.Sessao.EndSessao;
using FSH.Modules.PAF.Features.v1.Sessao.SetGuiche;
using FSH.Modules.PAF.Features.v1.Sessao.SetUnidadeCorrente;
using FSH.Modules.PAF.Features.v1.Unidades.CreateUnidade;
using FSH.Modules.PAF.Features.v1.Unidades.DisableUnidade;
using FSH.Modules.PAF.Features.v1.Unidades.EnableUnidade;
using FSH.Modules.PAF.Features.v1.Unidades.GetAdministracaoUnidades;
using FSH.Modules.PAF.Features.v1.Unidades.ListUnidades;
using FSH.Modules.PAF.Features.v1.Unidades.ListUnidadesOperador;
using FSH.Modules.PAF.Features.v1.Unidades.UpdateUnidade;
using FSH.Modules.PAF.Features.v1.Usuarios;
using FSH.Modules.PAF.Features.v1.UsuariosUnidade;
using FSH.Modules.PAF.Features.v1.UsuariosUnidade.GetUsuarioUnidades;
using FSH.Modules.PAF.Features.v1.UsuariosUnidade.SetUsuarioUnidades;
using FSH.Modules.PAF.Midias;
using FSH.Modules.PAF.Features.v1.Midias;
using FSH.Modules.PAF.Features.v1.Midias.CreateMidia;
using FSH.Modules.PAF.Features.v1.Midias.EnableMidia;
using FSH.Modules.PAF.Features.v1.Midias.ListMidias;
using FSH.Modules.PAF.Features.v1.Midias.PedirEnvioMidia;
using FSH.Modules.PAF.Features.v1.Midias.TrocarArquivoMidia;
using FSH.Modules.PAF.Features.v1.Midias.UpdateMidia;
using FSH.Modules.PAF.Observabilidade;
using FSH.Modules.PAF.Parametros;
using FSH.Modules.PAF.Saude;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

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
        builder.Services.AddScoped<LeitorParametros>();
        // C10: the single writer of RF06 rows (upsert, hotkey rule, impersonation act).
        builder.Services.AddScoped<GravacaoParametros>();

        // S14: own PostgreSQL check with a ceiling, and the Valkey as Degraded (route paf/saude, seção 9).
        builder.Services.AddHealthChecks()
            .AddCheck<SaudePostgresPaf>(SaudePaf.NomePostgres, HealthStatus.Unhealthy, [SaudePaf.Etiqueta])
            .AddCheck<SaudeValkeyPaf>(SaudePaf.NomeValkey, HealthStatus.Degraded, [SaudePaf.Etiqueta]);
        CacheDeTenantSemValkey.Registrar(builder.Services);

        PermissionConstants.Register(PafPermissions.All);
        ConfigureCadastroServices(builder.Services);
        RegistrarCredencialDeEstacao(builder.Services);
        AddTempoReal(builder.Services);
        ConfigurarSessao(builder.Services);
        OpcoesMidia.Registrar(builder.Services);
        RegistrarCadastroDeMidia(builder.Services);
        LogSemDadosPessoais.Registrar(builder.Configuration);
    }

    // RF03 (M03): the store door with its deadline, and the envio confirmation shared by create and swap.
    private static void RegistrarCadastroDeMidia(IServiceCollection services)
    {
        services.AddScoped<ArmazenamentoMidia>();
        services.AddScoped<ConfirmacaoEnvioMidia>();
    }

    private static void MapearMidias(IEndpointRouteBuilder midias)
    {
        midias.MapRequestMediaUploadEndpoint();
        midias.MapRequestMediaSwapUploadEndpoint();
        midias.MapCreateMidiaEndpoint();
        midias.MapListMidiasEndpoint();
        midias.MapUpdateMidiaEndpoint();
        midias.MapSwapMidiaFileEndpoint();
        midias.MapEnableMidiaEndpoint();
        midias.MapDisableMidiaEndpoint();
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
        MapearFichas(cadastro);
        cadastro.MapRestoreAtendimentoEndpoint();

        // RF07 (E0): reads the caller's scope, never the current unit.
        cadastro.MapGetEstatisticaEndpoint();

        // RF08 user cadastro (C12).
        cadastro.MapUsuariosPafEndpoints();

        // RF03 (M03): mídia cadastro, upload by presigned URL.
        MapearMidias(cadastro);

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

        // S14: anonymous and outside the rate limit, like the kit's /health probes.
        endpoints.MapGroup("api/v{version:apiVersion}/paf")
            .WithTags("PAF Saúde")
            .WithApiVersionSet(versionSet)
            .MapSaudeEndpoint();

        // S08: licence history and the session leftovers nobody else releases (ARQ seções 5 e 9). The migrator host
        // runs no job server. The CancellationToken argument is a placeholder Hangfire swaps for the server's token.
        endpoints.ServiceProvider.GetService<IRecurringJobManager>()?.AddOrUpdate(
            ManutencaoLicencas.IdRecorrente,
            Job.FromExpression<ManutencaoLicencas>(j => j.ExecutarAsync(default)),
            ManutencaoLicencas.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    // Licença, sessão única, unidade corrente e guichê (RF22, ADR 0006, ADR 0008).
    private static void ConfigurarSessao(IServiceCollection services)
    {
        services.AddScoped<SessaoCorrente>();
        // S10B: the impersonation grant's own branch (current unit keyed by the grant, no licence).
        services.AddScoped<ConcessaoCorrente>();
        services.AddScoped<UnidadeDaConcessao>();
        services.AddScoped<Licencas>();
        // S09: licence set in the Valkey and its rebuild from the PostgreSQL (seção 9).
        services.AddSingleton(sp => new ConjuntoLicencas(
            sp.GetServices<IConnectionMultiplexer>(),
            TimeSpan.FromMinutes(Licencas.PrazoMarcaMinutos),
            sp.GetRequiredService<ILogger<ConjuntoLicencas>>()));
        services.AddSingleton(sp => new ReconstrucaoLicencas(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ConjuntoLicencas>(),
            ReconstrucaoLicencas.IntervaloVerificacao,
            sp.GetRequiredService<ILogger<ReconstrucaoLicencas>>()));
        services.AddHostedService(sp => sp.GetRequiredService<ReconstrucaoLicencas>());
        services.AddScoped<EstadoSessao>();
        services.AddScoped<RevogacaoSessao>();
        services.AddScoped<ReocupacaoSessao>();
        services.AddScoped<IPipelineBehavior<GenerateTokenCommand, TokenResponse>, GenerateTokenLicenseBehavior>();
        services.AddScoped<IPipelineBehavior<RefreshTokenCommand, RefreshTokenCommandResponse>, RefreshTokenLicenseBehavior>();
        // S11A (D28): AtoImpersonacao of every write; the open generic only closes over IEscritaDeCadastro commands.
        services.AddScoped<AtosImpersonacao>();
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(AtoImpersonacaoBehavior<,>));
        // S08: recurring job of ConsumoLicenca, expired marks and orphan guichê links.
        services.AddScoped<ManutencaoLicencas>();
    }

    // RF12 fichas screens (F04): person operations outside the guichê, each under its own FichasDoDia permission.
    private static void MapearFichas(IEndpointRouteBuilder fichas)
    {
        fichas.MapVoidAtendimentoDaFichaEndpoint();
        // F06: the RF12 list and the reprint of a stored ficha, under FichasDoDia.View.
        fichas.MapListAtendimentosDoDiaEndpoint();
        fichas.MapGetFichaImpressaoEndpoint();
        // RF13 (F07): FichasEmAtendimento.View; its cancel button reuses the F04 route above.
        fichas.MapListFichasInProgressEndpoint();
        // RF18 (F14): read only; its cancel and reabilitar are the F04 and F05 routes.
        fichas.MapGetFichaBySenhaEndpoint();
    }

    private static void MapearSessao(IEndpointRouteBuilder sessao)
    {
        sessao.MapSetUnidadeCorrenteEndpoint();
        sessao.MapSetGuicheEndpoint();
        sessao.MapEndSessaoEndpoint();
        sessao.MapGetAvisoLicencaEndpoint();
    }

    private static void MapearGuiche(IEndpointRouteBuilder guiche)
    {
        guiche.MapRequestNextFichaEndpoint();
        guiche.MapResendCallEndpoint();
        guiche.MapStartAtendimentoEndpoint();
        guiche.MapEndAtendimentoEndpoint();
        guiche.MapVoidAtendimentoEndpoint();
        guiche.MapListMinhasUnidadesEndpoint();
        guiche.MapListGuichesDaUnidadeEndpoint();
        guiche.MapGetPostoGuicheEndpoint();
        guiche.MapListForwardDestinationsEndpoint();
        guiche.MapSendAtendimentoToFilasEndpoint();
        guiche.MapListWaitingFichasEndpoint();
        guiche.MapRequestChosenFichaEndpoint();
        guiche.MapRequestVipFichaEndpoint();
        guiche.MapListVipFilasEndpoint();
        // C10: hotkeys under the guichê permission.
        guiche.MapGetTeclasGuicheEndpoint();
    }

    // Cadastros and unit scope.
    private static void ConfigureCadastroServices(IServiceCollection services)
    {
        services.AddScoped<EscopoUsuario>();
        services.AddScoped<PafDemoSeeder>();

        // RF08 user cadastro (C12).
        services.AddScoped<ConcessaoDeEscopo>();
        services.AddScoped<PapeisDoUsuarioPaf>();
        services.AddScoped<AtendimentosEmCursoDoUsuario>();

        // RF07 types 1 to 6 (E1).
        services.AddScoped<IConsultaEstatistica, ConsultaAtendimentosPorTempoDeEspera>();
        services.AddScoped<IConsultaEstatistica, ConsultaAtendimentosPorTempoDeAtendimento>();
        services.AddScoped<IConsultaEstatistica, ConsultaAtendimentosPorGuiche>();
        services.AddScoped<IConsultaEstatistica, ConsultaAtendimentosPorAtendente>();
        services.AddScoped<IConsultaEstatistica, ConsultaAtendimentosPorHorarioDePico>();
        services.AddScoped<IConsultaEstatistica, ConsultaAtendimentosNaoEfetuadosOuCancelados>();
        // E2: RF07 types 7 and 8, over Fichas.DataOperacional.
        services.AddScoped<IConsultaEstatistica, FichasGeradasPorHorarioDePico>();
        services.AddScoped<IConsultaEstatistica, FichasEmitidasPorGuiche>();
    }

    // Cadastros and unit scope.
    private static void MapCadastroEndpoints(IEndpointRouteBuilder group)
    {
        group.MapCreateUnidadeEndpoint();
        group.MapListUnidadesEndpoint();
        group.MapUpdateUnidadeEndpoint();
        group.MapEnableUnidadeEndpoint();
        group.MapDisableUnidadeEndpoint();
        group.MapGetAdministracaoUnidadesEndpoint();

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
        group.MapCreateSetorEndpoint();
        group.MapListSetoresEndpoint();
        group.MapUpdateSetorEndpoint();

        group.MapCreateEstacaoEndpoint();
        group.MapListEstacoesEndpoint();
        group.MapUpdateEstacaoEndpoint();
        group.MapEnableEstacaoEndpoint();
        group.MapDisableEstacaoEndpoint();
        group.MapGetImpactoDesativacaoEstacaoEndpoint();
        group.MapReleaseGuicheEndpoint();

        group.MapCreateContratoUnidadeEndpoint();
        group.MapUpdateContratoUnidadeEndpoint();
        group.MapListContratosUnidadeEndpoint();
        group.MapListUnidadesOperadorEndpoint();

        group.MapGetUsuarioUnidadesEndpoint();
        group.MapSetUsuarioUnidadesEndpoint();

        // C10: RF06 parameters (empresa default, unidade override).
        group.MapListParametrosEndpoint();
        group.MapSetParametroEmpresaEndpoint();
        group.MapSetParametroUnidadeEndpoint();
        group.MapRemoveParametroUnidadeEndpoint();
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
        RegistrarVidaPelaConexao(services);
        services.AddSingleton<PafHubFilter>();
        // AddHubOptions, not Configure<HubOptions<PafHub>>: only it registers HubOptionsSetup<PafHub>, which marks the
        // per-hub options as set; without that mark SignalR falls back to the global options and the filter never runs.
        services.AddSignalR().AddHubOptions<PafHub>(options => options.AddFilter<PafHubFilter>());
        services.AddIntegrationEventHandlers(typeof(PafModule).Assembly);
    }

    // Connection registry in Valkey and the per-instance loop that keeps licence and guichê alive by the connection,
    // releasing them after the reconnect grace period (seção 7, seção 9, RF22).
    private static void RegistrarVidaPelaConexao(IServiceCollection services)
    {
        OpcoesSessaoPaf.Registrar(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<RegistroConexoes>();
        services.AddSingleton<RenovacaoPelaConexao>();
        services.AddHostedService(sp => sp.GetRequiredService<RenovacaoPelaConexao>());
    }

    private static void MapTempoReal(IEndpointRouteBuilder endpoints)
    {
        // The kit enables this on no hub: a connection would outlive the token that opened it, and an
        // expired impersonation grant, which raises no event, would keep receiving the unit's events.
        endpoints.MapHub<PafHub>(PafHubRota.Caminho, options => options.CloseOnAuthenticationExpiration = true);
    }
}
