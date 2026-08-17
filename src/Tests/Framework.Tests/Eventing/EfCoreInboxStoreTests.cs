using FSH.Framework.Eventing.Inbox;
using Shouldly;
using Xunit;

namespace Framework.Tests.Eventing;

public class EfCoreInboxStoreTests
{
    [Fact]
    public async Task MarkProcessedAsync_Then_HasProcessedAsync_Returns_True()
    {
        await using var context = EventingTestContext.CreateSqlite();
        var store = new EfCoreInboxStore(context, TimeProvider.System);
        var eventId = Guid.CreateVersion7();

        (await store.HasProcessedAsync(eventId, "HandlerA", CancellationToken.None))
            .ShouldBeFalse();

        await store.MarkProcessedAsync(eventId, "HandlerA", "acme", "SomeEvent", CancellationToken.None);

        (await store.HasProcessedAsync(eventId, "HandlerA", CancellationToken.None))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Dedup_Key_Is_Per_Handler()
    {
        await using var context = EventingTestContext.CreateSqlite();
        var store = new EfCoreInboxStore(context, TimeProvider.System);
        var eventId = Guid.CreateVersion7();

        await store.MarkProcessedAsync(eventId, "HandlerA", "acme", "SomeEvent", CancellationToken.None);

        (await store.HasProcessedAsync(eventId, "HandlerB", CancellationToken.None))
            .ShouldBeFalse("the inbox key is {eventId, handlerName}; a second handler must still run");
    }

    [Fact]
    public async Task MarkProcessedAsync_Is_Idempotent()
    {
        await using var context = EventingTestContext.CreateSqlite();
        var store = new EfCoreInboxStore(context, TimeProvider.System);
        var eventId = Guid.CreateVersion7();

        await store.MarkProcessedAsync(eventId, "HandlerA", "acme", "SomeEvent", CancellationToken.None);
        await store.MarkProcessedAsync(eventId, "HandlerA", "acme", "SomeEvent", CancellationToken.None);

        context.InboxMessages.Count().ShouldBe(1, "a repeated mark for the same {eventId, handlerName} must not insert a second row");
    }
}
