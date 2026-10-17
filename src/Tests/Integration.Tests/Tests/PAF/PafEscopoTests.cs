using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Filas;
using FSH.Modules.PAF.Contracts.v1.Unidades;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// ADR 0003 through the real HTTP pipeline: the consolidated read turns off only the unit filter, so another
/// tenant's rows never come back, and normal reads see only the unidade of X-Paf-Unidade, validated against
/// the caller's UsuarioUnidade scope.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafEscopoTests(FshWebApplicationFactory factory)
{
    private readonly PafApi _api = new(factory);

    [Fact]
    public async Task LeituraConsolidada_NuncaDevolveFilaDeOutroTenant_MesmoApontandoParaUnidadeDoEscopo()
    {
        using var root = await _api.RootAsync();
        var unidade = await PafApi.CriarUnidadeAsync(root);
        var prioridade = await PafApi.CriarPrioridadeAsync(root, unidade, "Normal");
        var filaDoRoot = await PafApi.CriarFilaAsync(root, unidade, "Caixa", prioridade, "A");

        // Tenant B's fila points at the root's unidade on purpose: only the tenant filter separates them, which
        // is exactly what a parameterless IgnoreQueryFilters() would drop.
        string nomeDeB = $"Fila-de-B-{Guid.NewGuid():N}";
        var filaDeB = PlantarFilaEmOutroTenant(unidade, nomeDeB);

        using var response = await PafApi.EnviarAsync(root, HttpMethod.Get, "/filas/consolidadas", null, null);
        string corpo = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, corpo);
        var filas = System.Text.Json.JsonSerializer.Deserialize<List<FilaDto>>(corpo, JsonWeb)!;

        filas.ShouldContain(f => f.Id == filaDoRoot);
        filas.ShouldNotContain(f => f.Id == filaDeB);
        corpo.ShouldNotContain(nomeDeB);
        (await ContarFilasNoTenantAsync(TenantB, filaDeB)).ShouldBe(1, "the tenant B row must exist, or the test proves nothing");
    }

    [Fact]
    public async Task LeituraNormal_SoVeAUnidadeDoCabecalho_ESemCabecalhoNaoVeNada()
    {
        using var root = await _api.RootAsync();
        var u1 = await PafApi.CriarUnidadeAsync(root);
        var u2 = await PafApi.CriarUnidadeAsync(root);
        var f1 = await PafApi.CriarFilaAsync(root, u1, "Caixa", await PafApi.CriarPrioridadeAsync(root, u1, "Normal"), "A");
        var f2 = await PafApi.CriarFilaAsync(root, u2, "Caixa", await PafApi.CriarPrioridadeAsync(root, u2, "Normal"), "A");

        var deU1 = await PafApi.FilasAsync(root, u1);
        deU1.ShouldAllBe(f => f.UnidadeId == u1);
        deU1.ShouldContain(f => f.Id == f1);
        deU1.ShouldNotContain(f => f.Id == f2);

        var deU2 = await PafApi.FilasAsync(root, u2);
        deU2.ShouldContain(f => f.Id == f2);
        deU2.ShouldNotContain(f => f.Id == f1);

        (await PafApi.FilasAsync(root, null)).ShouldBeEmpty("no current unidade is fail-closed");

        var consolidadas = await PafApi.OkAsync<List<FilaDto>>(root, HttpMethod.Get, "/filas/consolidadas", null, null);
        consolidadas.ShouldContain(f => f.Id == f1);
        consolidadas.ShouldContain(f => f.Id == f2);
    }

    [Fact]
    public async Task CabecalhoDeUnidadeForaDoEscopo_Recusado403_EMalformado400()
    {
        using var root = await _api.RootAsync();

        using var fora = await PafApi.EnviarAsync(root, HttpMethod.Get, "/filas", Guid.NewGuid(), null);
        fora.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await fora.Content.ReadAsStringAsync());

        // A unidade that exists in the tenant but is not in the caller's scope is refused the same way.
        var semEscopo = PlantarUnidadeForaDoEscopo();
        using var naoConcedida = await PafApi.EnviarAsync(root, HttpMethod.Get, "/filas", semEscopo, null);
        naoConcedida.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{PafApi.Base}/filas");
        request.Headers.Add(PafApi.CabecalhoUnidade, "nao-e-guid");
        using var malformado = await root.SendAsync(request);
        malformado.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task EscritaDeEscopoDeUnidadeSemUnidadeCorrente_Recusada400()
    {
        using var root = await _api.RootAsync();

        using var response = await PafApi.EnviarAsync(root, HttpMethod.Post, "/prioridades", null,
            new FSH.Modules.PAF.Contracts.v1.Prioridades.CreatePrioridadeCommand("Normal", 0));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain(PafApi.CabecalhoUnidade);
    }

    [Fact]
    public async Task ListaDeUnidades_TrazOEscopoDoChamador_ComAUnidadeRecemCriada()
    {
        using var root = await _api.RootAsync();
        var unidade = await PafApi.CriarUnidadeAsync(root);

        var unidades = await PafApi.OkAsync<List<UnidadeDto>>(root, HttpMethod.Get, "/unidades", null, null);

        unidades.ShouldContain(u => u.Id == unidade && u.Ativa);
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonWeb = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static readonly AppTenantInfo TenantB = new($"paf-b-{Guid.NewGuid():N}"[..20], "PAF tenant B", string.Empty, "b@paf.test", "paf.b");

    private Guid PlantarFilaEmOutroTenant(Guid unidadeDoRoot, string nome)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(TenantB);
        var db = scope.ServiceProvider.GetRequiredService<PafDbContext>();
        var fila = Fila.Criar(unidadeDoRoot, nome, nome, setorId: null);
        db.Filas.Add(fila);
        db.SaveChanges();
        return fila.Id;
    }

    private Guid PlantarUnidadeForaDoEscopo()
    {
        using var scope = factory.Services.CreateScope();
        var root = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
            .GetAsync(TestConstants.RootTenantId).GetAwaiter().GetResult();
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(root!);
        var db = scope.ServiceProvider.GetRequiredService<PafDbContext>();
        var unidade = Unidade.Criar($"Sem escopo {Guid.NewGuid():N}", null);
        db.Unidades.Add(unidade);
        db.SaveChanges();
        return unidade.Id;
    }

    private async Task<int> ContarFilasNoTenantAsync(AppTenantInfo tenant, Guid filaId)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(tenant);
        var db = scope.ServiceProvider.GetRequiredService<PafDbContext>();
        return await db.Filas.IgnoreQueryFilters([PafQueryFilters.Unidade]).CountAsync(f => f.Id == filaId);
    }
}
