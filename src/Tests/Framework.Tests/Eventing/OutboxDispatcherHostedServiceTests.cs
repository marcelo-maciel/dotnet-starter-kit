using FSH.Framework.Eventing;
using FSH.Framework.Eventing.Abstractions;
using FSH.Framework.Eventing.Outbox;
using FSH.Framework.Eventing.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Framework.Tests.Eventing;

public class OutboxDispatcherHostedServiceTests
{
    private static readonly EventingDrainTarget Default = new(null, null);
    private static readonly EventingDrainTarget Acme = new("acme", "Host=acme;Database=acme;Username=u;Password=p");

    [Fact]
    public async Task Drains_Once_Per_Target()
    {
        var targets = new List<EventingDrainTarget> { Default, Acme };
        var recording = new RecordingDrainScope();
        var store = EmptyStore();

        await RunOneCycleAsync(targets, recording, store);

        recording.Begun.ShouldBe(targets, "every database holding outbox rows must be drained each cycle");
        await store.Received(2).ClaimBatchAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Single_Target_Behaves_As_Before()
    {
        var recording = new RecordingDrainScope();
        var store = EmptyStore();

        await RunOneCycleAsync([Default], recording, store);

        recording.Begun.ShouldHaveSingleItem();
        await store.Received(1).ClaimBatchAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task One_Unreachable_Database_Does_Not_Stop_The_Others()
    {
        var recording = new RecordingDrainScope();
        var store = Substitute.For<IOutboxStore>();
        store.ClaimBatchAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new InvalidOperationException("acme is down"),
                _ => Task.FromResult<IReadOnlyList<OutboxMessage>>([]));

        await RunOneCycleAsync([Acme, Default], recording, store);

        recording.Begun.Count.ShouldBe(
            2,
            "a tenant database being down must not strand every other tenant's outbox for the cycle");
        recording.Disposed.ShouldBe(2, "each drain scope must be released even when the drain throws");
    }

    private static IOutboxStore EmptyStore()
    {
        var store = Substitute.For<IOutboxStore>();
        store.ClaimBatchAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OutboxMessage>>([]));
        return store;
    }

    private static async Task RunOneCycleAsync(
        IReadOnlyList<EventingDrainTarget> targets,
        IEventingDrainScope drainScope,
        IOutboxStore store)
    {
        var provider = Substitute.For<IEventingDrainTargetProvider>();
        provider.GetTargetsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(targets));

        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(Substitute.For<IEventBus>());
        services.AddSingleton<IEventSerializer, JsonEventSerializer>();
        services.AddSingleton(provider);
        services.AddSingleton(drainScope);
        services.AddSingleton(Options.Create(new EventingOptions()));
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddScoped<OutboxDispatcher>();

        await using var root = services.BuildServiceProvider();

        using var sut = new OutboxDispatcherHostedService(
            root.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new EventingOptions()),
            NullLogger<OutboxDispatcherHostedService>.Instance);

        await sut.DispatchOutboxAsync(CancellationToken.None);
    }

    private sealed class RecordingDrainScope : IEventingDrainScope
    {
        public List<EventingDrainTarget> Begun { get; } = [];

        public int Disposed { get; private set; }

        public IDisposable Begin(EventingDrainTarget target)
        {
            Begun.Add(target);
            return new Handle(this);
        }

        private sealed class Handle : IDisposable
        {
            private readonly RecordingDrainScope _owner;

            public Handle(RecordingDrainScope owner) => _owner = owner;

            public void Dispose() => _owner.Disposed++;
        }
    }
}
