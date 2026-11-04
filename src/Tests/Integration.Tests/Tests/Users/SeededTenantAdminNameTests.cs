using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;

namespace Integration.Tests.Tests.Users;

/// <summary>
/// The tenant admin is seeded with the client's name as its last name, and the profile validator caps a last name at
/// 50 characters. The profile screen resends the last name on every save, so a longer client name must not reach the
/// seeded admin whole, or that admin could never save the profile again.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class SeededTenantAdminNameTests(FshWebApplicationFactory factory)
{
    private const int LastNameMaxLength = 50;
    private readonly AuthHelper _auth = new(factory);

    [Fact]
    public async Task TenantWithALongName_SeedsAnAdminWhoseLastNameFitsAndWhoseProfileCanBeSaved()
    {
        // Arrange — 49 characters, then a space at the cut and more text: the cut lands on the space, which goes.
        var sufixo = Guid.NewGuid().ToString("N")[..8];
        var cabeca = $"Rede {sufixo} Comércio de Alimentos do Interior Paulista"[..(LastNameMaxLength - 1)];
        cabeca.ShouldNotEndWith(" ");
        var nome = $"{cabeca} Filial Centro Norte e Região Metropolitana";
        nome.Length.ShouldBeGreaterThan(LastNameMaxLength);
        var tenantId = $"longo-{sufixo}";
        var adminEmail = $"longo-{sufixo}@tenant.com";
        using (var rootClient = await _auth.CreateRootAdminClientAsync())
        {
            using var created = await rootClient.PostAsJsonAsync(TestConstants.TenantsBasePath, new
            {
                id = tenantId,
                name = nome,
                connectionString = (string?)null,
                adminEmail,
                adminPassword = TestConstants.DefaultPassword,
                issuer = $"{tenantId}.issuer"
            });
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
            await TenantProvisioningWait.WaitForProvisioningAsync(rootClient, tenantId);
        }
        using var admin = await _auth.CreateAuthenticatedClientAsync(adminEmail, TestConstants.DefaultPassword, tenantId);

        // Act
        using var profile = await admin.GetAsync($"{TestConstants.IdentityBasePath}/profile");
        profile.StatusCode.ShouldBe(HttpStatusCode.OK, await profile.Content.ReadAsStringAsync());
        var seeded = await profile.DeserializeAsync<UserDto>();
        using var saved = await admin.PutAsJsonAsync($"{TestConstants.IdentityBasePath}/profile", new
        {
            firstName = seeded.FirstName,
            lastName = seeded.LastName,
            phoneNumber = "11999990000"
        });

        // Assert
        seeded.FirstName.ShouldBe("Administrador");
        seeded.LastName.ShouldBe(cabeca, "cut at 50 and the trailing space trimmed");
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
    }
}
