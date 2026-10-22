using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Framework.Shared.Persistence;
using FSH.Modules.Multitenancy;
using FSH.Modules.Multitenancy.Provisioning;
using FSH.Modules.Multitenancy.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Multitenancy.Tests.Services;

/// <summary>
/// The API container runs with <c>TZ=America/Sao_Paulo</c>. System.Text.Json reads a validity sent with a
/// non-Z offset as <see cref="DateTimeKind.Local"/>, already moved to that zone; relabelling it as UTC
/// would store the Brasília wall clock as UTC, three hours early. The Local value here is built from an
/// explicit offset, so the expected instant holds in any machine zone; the relabelling it guards against
/// is only wrong, and only caught, where the machine zone is not UTC.
/// </summary>
public sealed class TenantServiceValidityKindTests
{
    private static readonly DateTime ExpectedUtc = new(2026, 5, 1, 3, 0, 0, DateTimeKind.Utc);

    private readonly IMultiTenantStore<AppTenantInfo> _store = Substitute.For<IMultiTenantStore<AppTenantInfo>>();
    private readonly IServiceProvider _serviceProvider = Substitute.For<IServiceProvider>();

    private TenantService CreateSut()
    {
        _serviceProvider
            .GetService(typeof(IEnumerable<IMultiTenantStore<AppTenantInfo>>))
            .Returns(Array.Empty<IMultiTenantStore<AppTenantInfo>>());

        return new TenantService(
            _store,
            Options.Create(new DatabaseOptions { ConnectionString = "Host=localhost;Database=fsh;Username=x;Password=y" }),
            _serviceProvider,
            dbContext: null!,
            provisioningService: null!,
            TimeProvider.System,
            Options.Create(new TenantBillingOptions()),
            NullLogger<TenantService>.Instance);
    }

    [Fact]
    public async Task AdjustValidity_Should_ConvertALocalValue_ToTheSameUtcInstant()
    {
        // What System.Text.Json yields for "2026-05-01T00:00:00-03:00": Kind=Local, in the machine's zone.
        var local = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.FromHours(-3)).LocalDateTime;
        local.Kind.ShouldBe(DateTimeKind.Local);
        var tenant = new AppTenantInfo("acme", "Acme", connectionString: null, adminEmail: "admin@acme.test");
        _store.GetAsync("acme").Returns(tenant);

        var stored = await CreateSut().AdjustValidityAsync("acme", local, CancellationToken.None);

        stored.ShouldBe(ExpectedUtc);
        stored.Kind.ShouldBe(DateTimeKind.Utc);
        tenant.ValidUpto.ShouldBe(ExpectedUtc);
    }

    [Fact]
    public async Task AdjustValidity_Should_ReadAnUnspecifiedValue_AsUtc()
    {
        // A bare date or a value without offset is UTC by contract, as before.
        var tenant = new AppTenantInfo("acme", "Acme", connectionString: null, adminEmail: "admin@acme.test");
        _store.GetAsync("acme").Returns(tenant);

        var stored = await CreateSut().AdjustValidityAsync(
            "acme", new DateTime(2026, 5, 1, 3, 0, 0, DateTimeKind.Unspecified), CancellationToken.None);

        stored.ShouldBe(ExpectedUtc);
        stored.Kind.ShouldBe(DateTimeKind.Utc);
    }
}
