using FSH.Framework.Eventing.Abstractions;

namespace FSH.Framework.Eventing;

/// <summary>
/// No-op drain scope used when no multitenancy composition is wired: there is only the default
/// database, so nothing needs installing before a drain pass.
/// </summary>
public sealed class NullEventingDrainScope : IEventingDrainScope
{
    private static readonly IDisposable Noop = new NoopScope();

    public IDisposable Begin(EventingDrainTarget target) => Noop;

    private sealed class NoopScope : IDisposable
    {
        public void Dispose()
        {
            // Nothing to restore — the ambient context was never touched.
        }
    }
}
