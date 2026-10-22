using System.Reflection;
using FSH.Framework.Web.Modules;
using FSH.Modules.PAF;
using FSH.Modules.PAF.Data;

namespace PAF.Tests;

public sealed class PafModuleTests
{
    [Fact]
    public void Assembly_Should_DeclarePafModule_When_Loaded()
    {
        // Act
        FshModuleAttribute? attribute = typeof(PafModule).Assembly.GetCustomAttribute<FshModuleAttribute>();

        // Assert
        attribute.ShouldNotBeNull();
        attribute.ModuleType.ShouldBe(typeof(PafModule));
        attribute.Order.ShouldBe(900);
    }

    [Fact]
    public void DbContext_Should_UsePafSchema()
    {
        PafDbContext.Schema.ShouldBe("paf");
    }
}
