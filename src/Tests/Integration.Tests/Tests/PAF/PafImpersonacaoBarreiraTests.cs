using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Contracts.v1.Impersonation;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Prioridades;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Barreira de impersonação (RF22, RNF15, decisão de 06/10/2026): só a impersonação da Codefi (<c>act_tenant</c> raiz)
/// opera o PAF; a do administrador do cliente recebe 403 nas rotas do PAF e no hub, e segue valendo nas telas do kit.
/// O filtro do hub revalida a concessão a cada chamada. The impersonated user holds the permissions of every route
/// exercised, so each 403 comes from the barrier (module middleware runs before authorization), not from a policy.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafImpersonacaoBarreiraTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Impersonacao = TestConstants.IdentityBasePath + "/impersonation";

    private readonly PafTenantCliente _tenant = new(factory, "paf-imp");

    public Task InitializeAsync() => _tenant.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AdminDoCliente_Impersonando_RecusadoNasRotasDoPaf_SemGravar_EAceitoNoKit()
    {
        var (unidade, alvo) = await AlvoComPermissoesAsync();
        using var suporte = _tenant.Cliente(await IniciarAsync(_tenant.Admin, alvo));

        using var posto = await suporte.GetAsync($"{PafApi.Base}/guiche/posto");
        await RecusadaPelaBarreiraAsync(posto);

        using var cadastro = await PafApi.EnviarAsync(suporte, HttpMethod.Post, "/prioridades", unidade, new CreatePrioridadeCommand("Imp", 0));
        await RecusadaPelaBarreiraAsync(cadastro);
        (await PrioridadesAsync(unidade)).ShouldBe(0, "a recusa vem antes do tratador: nenhuma linha gravada");

        // Routing is case-insensitive, so the barrier must be too.
        using var maiusculas = await suporte.GetAsync("/API/V1/PAF/guiche/posto");
        await RecusadaPelaBarreiraAsync(maiusculas);

        using var negociar = await suporte.PostAsync($"{PafHubRota.Caminho}/negotiate?negotiateVersion=1", content: null);
        await RecusadaPelaBarreiraAsync(negociar);

        using var perfil = await suporte.GetAsync($"{TestConstants.IdentityBasePath}/profile");
        perfil.StatusCode.ShouldBe(HttpStatusCode.OK, "as telas do kit continuam aceitando a impersonação do administrador do cliente");
    }

    [Fact]
    public async Task Codefi_Impersonando_OperaOPaf()
    {
        var (unidade, alvo) = await AlvoComPermissoesAsync();
        using var root = await new AuthHelper(factory).CreateRootAdminClientAsync();
        using var suporte = _tenant.Cliente(await IniciarAsync(root, alvo));

        // guiche/posto needs the impersonation's own unit state, which arrives with S10B; the scope read does not.
        using var unidades = await suporte.GetAsync($"{PafApi.Base}/guiche/unidades");
        unidades.StatusCode.ShouldBe(HttpStatusCode.OK, await unidades.Content.ReadAsStringAsync());
        (await unidades.Content.ReadAsStringAsync()).ShouldContain(unidade.ToString());

        await PafApi.OkAsync<Guid>(suporte, HttpMethod.Post, "/prioridades", unidade, new CreatePrioridadeCommand("Imp", 0));
        (await PrioridadesAsync(unidade)).ShouldBe(1);
    }

    [Fact]
    public async Task Filtro_ImpersonacaoDoAdminDoCliente_AbortaERecusa()
    {
        var (_, alvo) = await AlvoComPermissoesAsync();
        var token = await IniciarAsync(_tenant.Admin, alvo);

        var resultado = await InvocarFiltroAsync(Principal(token));

        resultado.Erro.ShouldBeOfType<HubException>().Message.ShouldBe(PafHubFilter.MensagemRecusada);
        resultado.Abortada.ShouldBeTrue();
    }

    [Fact]
    public async Task Filtro_ImpersonacaoSemActTenant_AbortaERecusa()
    {
        var (_, alvo) = await AlvoComPermissoesAsync();
        using var root = await new AuthHelper(factory).CreateRootAdminClientAsync();
        var claims = new JwtSecurityTokenHandler().ReadJwtToken(await IniciarAsync(root, alvo)).Claims
            .Where(c => c.Type != ClaimConstants.ActorTenant);

        var resultado = await InvocarFiltroAsync(new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Bearer")));

        resultado.Abortada.ShouldBeTrue("sem act_tenant a barreira fecha");
    }

    [Fact]
    public async Task Filtro_ConcessaoDaCodefiRevogada_ProximaChamadaAbortaERecusa()
    {
        var (_, alvo) = await AlvoComPermissoesAsync();
        using var root = await new AuthHelper(factory).CreateRootAdminClientAsync();
        var token = await IniciarAsync(root, alvo);
        var principal = Principal(token);

        (await InvocarFiltroAsync(principal)).Abortada.ShouldBeFalse("concessão vigente da Codefi passa pelo filtro");

        var jti = new JwtSecurityTokenHandler().ReadJwtToken(token).Id;
        using (var scope = factory.Services.CreateScope())
        {
            await InstalarRaizAsync(scope);
            var concessoes = scope.ServiceProvider.GetRequiredService<IImpersonationGrantService>();
            var concessao = (await concessoes.ListAsync(ImpersonationGrantStatus.Active, _tenant.TenantId, actorUserId: null, take: 50))
                .Single(g => g.Jti == jti);
            await concessoes.RevokeAsync(concessao.Id, "teste", revokedByUserName: null, reason: "teste");
        }

        var depois = await InvocarFiltroAsync(principal);
        depois.Erro.ShouldBeOfType<HubException>().Message.ShouldBe(PafHubFilter.MensagemRecusada);
        depois.Abortada.ShouldBeTrue("a revogação da concessão derruba a conexão na chamada seguinte");
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private async Task<(Guid Unidade, string Alvo)> AlvoComPermissoesAsync()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var alvo = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        await DarPapelAsync(alvo.UserId, PafPermissions.Prioridades.Create, PafPermissions.Guiches.Selecionar);
        return (unidade.UnidadeId, alvo.UserId);
    }

    private async Task<string> IniciarAsync(HttpClient ator, string alvo)
    {
        using var resposta = await ator.PostAsJsonAsync($"{Impersonacao}/start", new
        {
            targetUserId = alvo,
            targetTenantId = _tenant.TenantId,
            reason = "teste da barreira do PAF",
        });
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task RecusadaPelaBarreiraAsync(HttpResponseMessage resposta)
    {
        var corpo = await resposta.Content.ReadAsStringAsync();
        resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden, corpo);
        using var json = JsonDocument.Parse(corpo);
        json.RootElement.EnumerateObject()
            .Any(p => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == ImpersonacaoPaf.MensagemRecusada)
            .ShouldBeTrue($"o 403 vem da barreira de impersonação: {corpo}");
    }

    private async Task<int> PrioridadesAsync(Guid unidade)
    {
        using var scope = factory.Services.CreateScope();
        return await _tenant.Contexto(scope, unidade).Prioridades.CountAsync();
    }

    private static async Task InstalarRaizAsync(IServiceScope scope)
    {
        var raiz = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(MultitenancyConstants.Root.Id);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(raiz!);
    }

    private async Task DarPapelAsync(string usuarioId, params string[] permissoes)
    {
        var nome = $"paf-imp-{Guid.NewGuid():N}"[..20];
        using var criar = await _tenant.Admin.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/roles", new { id = string.Empty, name = nome, description = "PAF impersonação" });
        criar.StatusCode.ShouldBe(HttpStatusCode.OK, await criar.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await criar.Content.ReadAsStringAsync());
        var papelId = json.RootElement.GetProperty("id").GetString()!;

        using var definir = await _tenant.Admin.PutAsJsonAsync($"{TestConstants.IdentityBasePath}/{papelId}/permissions", new { roleId = papelId, permissions = permissoes });
        definir.StatusCode.ShouldBe(HttpStatusCode.OK, await definir.Content.ReadAsStringAsync());

        using var atribuir = await _tenant.Admin.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/users/{usuarioId}/roles", new
        {
            userId = usuarioId,
            userRoles = new[] { new { roleName = nome, enabled = true } },
        });
        atribuir.StatusCode.ShouldBe(HttpStatusCode.OK, await atribuir.Content.ReadAsStringAsync());
    }

    // The principal the JwtBearer handler would build: the token's claims, unvalidated, under an authenticated identity.
    private static ClaimsPrincipal Principal(string token) =>
        new(new ClaimsIdentity(new JwtSecurityTokenHandler().ReadJwtToken(token).Claims, authenticationType: "Bearer"));

    // Called directly, as in PafHubConexoesTests: over long polling the kit's JWT hook already refuses a revoked grant
    // at authentication, which would close the client by itself and hide a missing check in the filter.
    private async Task<(bool Abortada, Exception? Erro)> InvocarFiltroAsync(ClaimsPrincipal principal)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var servicos = scope.ServiceProvider;
        var contexto = new ContextoDeTeste(principal);
        using var hub = new PafHub(servicos.GetRequiredService<ConexoesPaf>(), servicos, NullLogger<PafHub>.Instance);
        var invocacao = new HubInvocationContext(contexto, servicos, hub, typeof(PafHub).GetMethod(nameof(PafHub.EntrarNoGuiche))!, []);
        try
        {
            await servicos.GetRequiredService<PafHubFilter>().InvokeMethodAsync(invocacao, _ => ValueTask.FromResult<object?>(null));
            return (contexto.Abortada, null);
        }
        catch (HubException ex)
        {
            return (contexto.Abortada, ex);
        }
    }

    private sealed class ContextoDeTeste(ClaimsPrincipal principal) : HubCallerContext
    {
        public bool Abortada { get; private set; }
        public override string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => principal;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() => Abortada = true;
    }
}
