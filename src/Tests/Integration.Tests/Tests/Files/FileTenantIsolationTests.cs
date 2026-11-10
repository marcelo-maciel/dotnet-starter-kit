using System.Security.Cryptography;
using FSH.Framework.Shared.Persistence;
using FSH.Modules.Files.Contracts.v1.DTOs;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;

namespace Integration.Tests.Tests.Files;

/// <summary>
/// Proves the Files module never leaks a file across tenant boundaries. On the shared database the
/// isolation is the Finbuckle filter BaseDbContext puts on FileAsset's shadow TenantId, so a lookup
/// from another tenant misses the row and the handler 404s ("file not found"), as long as no query
/// drops every filter to reach soft-deleted rows. These tests stand up other tenants and assert that
/// one tenant can neither read metadata, mint a download URL, list the trash nor restore a file of another.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class FileTenantIsolationTests
{
    private const string FilesBasePath = "/api/v1/files";
    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public FileTenantIsolationTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    #region Cross-tenant isolation

    [Fact]
    public async Task GetMetadata_Should_Return404_When_File_Owned_By_Different_Tenant()
    {
        // Arrange — root uploads a Private file; a second tenant comes online.
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var fileId = await UploadAndFinalizeAsync(rootClient, "tenant-a-secret.pdf", "application/pdf", 256, visibility: 1);

        using var otherClient = await CreateOtherTenantAdminClientAsync();

        // Act — tenant B asks for tenant A's file metadata.
        using var response = await otherClient.GetAsync($"{FilesBasePath}/{fileId}");

        // Assert — the row lives in a different schema, so the lookup misses → 404 (no existence leak).
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetDownloadUrl_Should_Return404_When_File_Owned_By_Different_Tenant()
    {
        // Arrange — even a Public file in tenant A must be invisible to tenant B; visibility is a
        // within-tenant concept, the schema boundary comes first.
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var fileId = await UploadAndFinalizeAsync(rootClient, "tenant-a-public.pdf", "application/pdf", 256, visibility: 0);

        using var otherClient = await CreateOtherTenantAdminClientAsync();

        // Act — tenant B tries to mint a presigned GET for tenant A's file.
        using var response = await otherClient.GetAsync($"{FilesBasePath}/{fileId}/url");

        // Assert — no row in tenant B's schema → 404, no presigned URL handed out.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetMetadata_Should_Return_File_For_Owning_Tenant_While_Other_Tenant_Gets_404()
    {
        // Arrange — same file, two tenants. This locks the positive AND negative halves together so
        // the 404 above can't be a false-positive from a broken upload.
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var fileId = await UploadAndFinalizeAsync(rootClient, "shared-id.pdf", "application/pdf", 256, visibility: 1);

        using var otherClient = await CreateOtherTenantAdminClientAsync();

        // Act
        using var ownerResponse = await rootClient.GetAsync($"{FilesBasePath}/{fileId}");
        using var crossResponse = await otherClient.GetAsync($"{FilesBasePath}/{fileId}");

        // Assert — owning tenant sees it, the other tenant does not.
        ownerResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var dto = await ownerResponse.DeserializeAsync<FileAssetDto>();
        dto.Id.ShouldBe(fileId);

        crossResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // Trash and restore are root-only permissions (FilesPermissions: ViewTrash and Restore are IsRoot), so the
    // caller below is always the root operator: on its own tenant, or on a client's through the tenant header.
    // IgnoreQueryFilters() with no argument also dropped the tenant filter, and the root operator then saw and
    // restored every client's trashed files from any tenant.

    [Fact]
    public async Task ListTrashedFiles_Should_KeepTwoClientTenantsApart_AndOutOfTheRootTrash()
    {
        // Arrange — the root operator trashes one file in its own tenant and one in each of two client tenants.
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var rootFile = await UploadAndTrashAsync(rootClient, "root-trashed.pdf");
        var tenantA = await ProvisionOtherTenantAsync();
        var tenantB = await ProvisionOtherTenantAsync();
        using var rootInA = await CreateRootClientInTenantAsync(tenantA);
        using var rootInB = await CreateRootClientInTenantAsync(tenantB);
        var fileA = await UploadAndTrashAsync(rootInA, "client-a-trashed.pdf");
        var fileB = await UploadAndTrashAsync(rootInB, "client-b-trashed.pdf");

        // Act
        var trashA = await TrashIdsAsync(rootInA);
        var trashB = await TrashIdsAsync(rootInB);
        var trashRoot = await TrashIdsAsync(rootClient);

        // Assert — each tenant's trash holds its own file and nothing else.
        trashA.ShouldBe([fileA]);
        trashB.ShouldBe([fileB]);
        trashRoot.ShouldContain(rootFile);
        trashRoot.ShouldNotContain(fileA, "the root tenant's trash holds root's files only");
        trashRoot.ShouldNotContain(fileB);
    }

    [Fact]
    public async Task Restore_Should_Return404_AndLeaveTheFileTrashed_When_File_Owned_By_Different_Tenant()
    {
        // Arrange — a trashed file of client A, and the root operator acting on client B.
        var tenantA = await ProvisionOtherTenantAsync();
        var tenantB = await ProvisionOtherTenantAsync();
        using var rootInA = await CreateRootClientInTenantAsync(tenantA);
        using var rootInB = await CreateRootClientInTenantAsync(tenantB);
        var fileA = await UploadAndTrashAsync(rootInA, "client-a-restore.pdf");

        // Act
        using var crossRestore = await rootInB.PostAsync($"{FilesBasePath}/{fileA}/restore", null);

        // Assert — not found, and client A's file is still in A's trash.
        crossRestore.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var ownerGet = await rootInA.GetAsync($"{FilesBasePath}/{fileA}");
        ownerGet.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the file must stay soft-deleted");
        (await TrashIdsAsync(rootInA)).ShouldContain(fileA);
    }

    [Fact]
    public async Task Restore_Should_Return404_When_LiveFile_Owned_By_Different_Tenant()
    {
        // Restoring a live file is a no-op 204 in its own tenant; from another tenant it must not answer
        // differently from an unknown id, or the endpoint tells which ids exist elsewhere.
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var fileId = await UploadAndFinalizeAsync(rootClient, "root-live.pdf", "application/pdf", 256, visibility: 1);
        using var rootInOther = await CreateRootClientInTenantAsync(await ProvisionOtherTenantAsync());

        using var crossRestore = await rootInOther.PostAsync($"{FilesBasePath}/{fileId}/restore", null);

        crossRestore.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static async Task<Guid> UploadAndTrashAsync(HttpClient client, string fileName)
    {
        var fileId = await UploadAndFinalizeAsync(client, fileName, "application/pdf", 256, visibility: 1);
        using var deleted = await client.DeleteAsync($"{FilesBasePath}/{fileId}");
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
        return fileId;
    }

    private static async Task<List<Guid>> TrashIdsAsync(HttpClient client)
    {
        using var response = await client.GetAsync($"{FilesBasePath}/trash?pageSize=200");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.DeserializeAsync<PagedResponse<FileAssetDto>>()).Items.Select(f => f.Id).ToList();
    }

    // The root operator's token with the tenant header: the override scopes each request to that client.
    private async Task<HttpClient> CreateRootClientInTenantAsync(string tenantId)
    {
        var rootToken = await _auth.GetRootAdminTokenAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", rootToken.AccessToken);
        client.DefaultRequestHeaders.Add("tenant", tenantId);
        return client;
    }

    private async Task<string> ProvisionOtherTenantAsync()
    {
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tenantId = $"file-trash-{uniqueId}";
        await CreateTenantAsync(rootClient, tenantId, $"file-trash-{uniqueId}@tenant.com");
        await TenantProvisioningWait.WaitForProvisioningAsync(rootClient, tenantId);
        return tenantId;
    }
    #endregion

    #region Tenant scaffolding (copied from WebhookTenantIsolationTests)

    private async Task<HttpClient> CreateOtherTenantAdminClientAsync()
    {
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var otherTenantId = $"file-iso-{uniqueId}";
        var otherAdminEmail = $"file-admin-{uniqueId}@tenant.com";

        await CreateTenantAsync(rootClient, otherTenantId, otherAdminEmail);
        await TenantProvisioningWait.WaitForProvisioningAsync(rootClient, otherTenantId);
        return await CreateTenantAdminClientWithRetryAsync(
            otherAdminEmail, TestConstants.DefaultPassword, otherTenantId);
    }

    private async Task<HttpClient> CreateTenantAdminClientWithRetryAsync(
        string email, string password, string tenant, int maxRetries = 30)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                return await _auth.CreateAuthenticatedClientAsync(email, password, tenant);
            }
            catch (HttpRequestException) when (i < maxRetries - 1)
            {
                await Task.Delay(1000);
            }
        }

        return await _auth.CreateAuthenticatedClientAsync(email, password, tenant);
    }

    private static async Task CreateTenantAsync(HttpClient rootClient, string tenantId, string adminEmail)
    {
        var response = await rootClient.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = tenantId,
            name = $"Tenant {tenantId}",
            connectionString = (string?)null,
            adminEmail,
            adminPassword = TestConstants.DefaultPassword,
            issuer = $"{tenantId}.issuer"
        });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.Created, $"Create tenant failed: {body}");
    }

    #endregion

    #region Upload helpers

    private static async Task<Guid> UploadAndFinalizeAsync(
        HttpClient client,
        string fileName,
        string contentType,
        int sizeBytes,
        int visibility)
    {
        byte[] bytes = new byte[sizeBytes];
        RandomNumberGenerator.Fill(bytes);

        using var urlResponse = await client.PostAsJsonAsync($"{FilesBasePath}/upload-url", new
        {
            ownerType = "MyFiles",
            ownerId = (Guid?)null,
            fileName,
            contentType,
            sizeBytes,
            visibility,
            category = "Document"
        });
        urlResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var presigned = await urlResponse.DeserializeAsync<PresignedUploadResponse>();

        using var raw = new HttpClient();
        using var put = new HttpRequestMessage(HttpMethod.Put, presigned.UploadUrl)
        {
            Content = new ByteArrayContent(bytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue(contentType) }
            }
        };
        using var putResp = await raw.SendAsync(put);
        putResp.EnsureSuccessStatusCode();

        using var finalize = await client.PostAsync($"{FilesBasePath}/{presigned.FileAssetId}/finalize", null);
        finalize.EnsureSuccessStatusCode();
        return presigned.FileAssetId;
    }

    #endregion
}
