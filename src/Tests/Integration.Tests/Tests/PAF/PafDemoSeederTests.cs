using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Provisionamento;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>The seed-demo data of the PAF (requisitos seção 9) is complete and a rerun writes nothing.</summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafDemoSeederTests(FshWebApplicationFactory factory)
{
    [Fact]
    public async Task Semear_GravaAMassaCompleta_ERodarDeNovoNaoGravaNada()
    {
        await SemearAsync();
        var primeira = await FotografarAsync();
        await SemearAsync();
        var segunda = await FotografarAsync();

        segunda.ShouldBe(primeira);
        primeira.Unidades.ShouldBe(1);
        primeira.Filas.ShouldBe(1);
        primeira.Prioridades.ShouldBe(1);
        primeira.Letras.ShouldBe(PafDemoSeeder.Letra);
        primeira.Estacoes.ShouldBe($"{TipoEstacao.EmissaoPainel},{TipoEstacao.Guiche}");
        primeira.PapeisDasFilas.ShouldBe("Emissao,Guiche,Painel");
        primeira.PrioridadesDeGuiche.ShouldBe(1);
        primeira.LimiteDoContrato.ShouldBe(PafDemoSeeder.LimiteSessoes);
        primeira.AdminNoEscopo.ShouldBe(1);
    }

    [Fact]
    public async Task Semear_TenantInexistente_FalhaSemGravar()
    {
        using var scope = factory.Services.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<PafDemoSeeder>();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            seeder.SemearAsync($"nao-existe-{Guid.NewGuid():N}"[..20], Guid.NewGuid(), CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() =>
            seeder.SemearAsync(TestConstants.RootTenantId, Guid.Empty, CancellationToken.None));
    }

    // A tenant seeded before Unidade.Inicial existed holds the demo unidade with Inicial = false (the column's default).
    [Fact]
    public async Task Semear_UnidadeDemoAnteriorAColunaInicial_AdotaASemDuplicar()
    {
        var (tenantId, adminEmail) = await CriarTenantAsync();
        var tenant = await ProvisionamentoTenantPaf.LerTenantAsync(factory.Services.GetRequiredService<IServiceScopeFactory>(), tenantId)
            ?? throw new InvalidOperationException($"Tenant '{tenantId}' não existe.");
        Guid antiga;
        Guid adminId;
        await using (var escopo = ProvisionamentoTenantPaf.AbrirEscopo(factory.Services.GetRequiredService<IServiceScopeFactory>(), tenant))
        {
            var db = escopo.ServiceProvider.GetRequiredService<PafDbContext>();
            (await db.Unidades.CountAsync()).ShouldBe(0);
            var unidade = Unidade.Criar(PafDemoSeeder.NomeUnidade, documentoFiscal: null);
            db.Unidades.Add(unidade);
            await db.SaveChangesAsync();
            antiga = unidade.Id;
            var usuarios = await escopo.ServiceProvider.GetRequiredService<IUserService>().GetListAsync(CancellationToken.None);
            adminId = Guid.Parse(usuarios.Single(u => u.Email == adminEmail).Id!);
        }

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<PafDemoSeeder>().SemearAsync(tenantId, adminId, CancellationToken.None);
        }

        await using (var escopo = ProvisionamentoTenantPaf.AbrirEscopo(factory.Services.GetRequiredService<IServiceScopeFactory>(), tenant))
        {
            var db = escopo.ServiceProvider.GetRequiredService<PafDbContext>();
            var unidades = await db.Unidades.AsNoTracking().Select(u => new { u.Id, u.Inicial }).ToListAsync();
            unidades.ShouldHaveSingleItem().Id.ShouldBe(antiga);
            unidades[0].Inicial.ShouldBeTrue();
            (await db.UsuariosUnidade.CountAsync(u => u.UsuarioId == adminId)).ShouldBe(1);
        }
    }

    private async Task<(string TenantId, string AdminEmail)> CriarTenantAsync()
    {
        var tenantId = $"paf-seed-{Guid.NewGuid().ToString("N")[..8]}";
        var adminEmail = $"admin-{tenantId}@paf.test";
        using var root = await new AuthHelper(factory).CreateRootAdminClientAsync();
        using var criar = await root.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = tenantId,
            name = $"Tenant {tenantId}",
            connectionString = (string?)null,
            adminEmail,
            adminPassword = TestConstants.DefaultPassword,
            issuer = $"{tenantId}.issuer",
        });
        criar.StatusCode.ShouldBe(HttpStatusCode.Created, await criar.Content.ReadAsStringAsync());
        await TenantProvisioningWait.WaitForProvisioningAsync(root, tenantId);
        return (tenantId, adminEmail);
    }

    private async Task SemearAsync()
    {
        var adminId = await new PafApi(factory).RootAdminIdAsync();
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PafDemoSeeder>()
            .SemearAsync(TestConstants.RootTenantId, adminId, CancellationToken.None);
    }

    private sealed record Fotografia(
        int Unidades, int Filas, int Prioridades, string Letras, string Estacoes, string PapeisDasFilas,
        int PrioridadesDeGuiche, int LimiteDoContrato, int AdminNoEscopo);

    private async Task<Fotografia> FotografarAsync()
    {
        var adminId = await new PafApi(factory).RootAdminIdAsync();
        using var scope = factory.Services.CreateScope();
        var root = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(root!);
        var db = scope.ServiceProvider.GetRequiredService<PafDbContext>();

        var unidades = await db.Unidades.Where(u => u.Nome == PafDemoSeeder.NomeUnidade).Select(u => u.Id).ToListAsync();
        scope.ServiceProvider.GetRequiredService<IUnidadeCorrente>().Definir(unidades.Single());

        var estacoes = await db.Estacoes.OrderBy(e => e.Tipo).Select(e => e.Tipo).ToListAsync();
        var papeis = await db.EstacaoFilas.OrderBy(ef => ef.Papel).Select(ef => ef.Papel).ToListAsync();
        return new Fotografia(
            unidades.Count,
            await db.Filas.CountAsync(),
            await db.Prioridades.CountAsync(),
            string.Join(",", await db.FilaPrioridades.Select(fp => fp.Letra).ToListAsync()),
            string.Join(",", estacoes.Select(t => t.ToString()).Order(StringComparer.Ordinal)),
            string.Join(",", papeis.Select(p => p.ToString()).Order(StringComparer.Ordinal)),
            await db.EstacaoPrioridades.CountAsync(),
            (await db.ContratosUnidade.SingleAsync()).LimiteSessoes,
            await db.UsuariosUnidade.CountAsync(u => u.UsuarioId == adminId && u.UnidadeId == unidades.Single()));
    }
}
