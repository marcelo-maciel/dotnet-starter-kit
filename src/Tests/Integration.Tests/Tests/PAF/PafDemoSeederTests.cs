using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
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
            seeder.SemearAsync($"nao-existe-{Guid.NewGuid():N}"[..20], TestConstants.RootAdminEmail, CancellationToken.None));
    }

    private async Task SemearAsync()
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PafDemoSeeder>()
            .SemearAsync(TestConstants.RootTenantId, TestConstants.RootAdminEmail, CancellationToken.None);
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
