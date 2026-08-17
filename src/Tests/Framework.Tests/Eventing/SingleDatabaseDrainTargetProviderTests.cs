using FSH.Framework.Eventing;
using Shouldly;
using Xunit;

namespace Framework.Tests.Eventing;

public class SingleDatabaseDrainTargetProviderTests
{
    [Fact]
    public async Task Returns_One_Default_Target()
    {
        var provider = new SingleDatabaseDrainTargetProvider();

        var targets = await provider.GetTargetsAsync(CancellationToken.None);

        targets.Count.ShouldBe(1, "a single-database deployment must drain exactly once per cycle");
        targets[0].TenantId.ShouldBeNull();
        targets[0].ConnectionString.ShouldBeNull("null means the configured default connection");
    }
}
