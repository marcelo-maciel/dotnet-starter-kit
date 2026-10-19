using FSH.Modules.PAF.Contracts.v1.Contratos;
using FSH.Modules.PAF.Contracts.v1.Unidades;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// A client tenant's admin gets the PAF person permissions, and never the root-only contract permission:
/// plan and licence limit are written by Codefi only (RF23).
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafPermissoesTests(FshWebApplicationFactory factory)
{
    private readonly AuthHelper _auth = new(factory);

    [Fact]
    public async Task AdminDeTenantClienteSemEscopoEmpresa_NaoCadastraUnidadeNemGravaContrato()
    {
        using var root = await _auth.CreateRootAdminClientAsync();
        string tenantId = $"paf-perm-{Guid.NewGuid():N}"[..20];
        string adminEmail = $"{tenantId}-admin@tenant.com";

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
        using var cliente = await _auth.CreateAuthenticatedClientAsync(adminEmail, TestConstants.DefaultPassword, tenantId);

        // The tenant admin without the escopo empresa profile holds Unidades.Create and is still refused (RF23).
        using var criacao = await PafApi.EnviarAsync(cliente, HttpMethod.Post, "/unidades", null, new CreateUnidadeCommand("Loja 1", null));
        criacao.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await criacao.Content.ReadAsStringAsync());
        var unidade = await new PafApi(factory).InserirUnidadeAsync(tenantId);
        (await PafApi.OkAsync<List<UnidadeDto>>(cliente, HttpMethod.Get, "/unidades", null, null)).ShouldNotContain(u => u.Id == unidade);

        using var contrato = await PafApi.EnviarAsync(cliente, HttpMethod.Post, "/contratos", null,
            new CreateContratoUnidadeCommand(unidade, new DateOnly(2026, 1, 1), null, "Pro", 999, "Recusar"));
        contrato.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Outside his scope the unit is not even readable through the header.
        using var leitura = await PafApi.EnviarAsync(cliente, HttpMethod.Get, "/contratos", unidade, null);
        leitura.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
