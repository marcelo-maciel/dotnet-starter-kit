namespace Integration.Tests.Infrastructure;

/// <summary>What a service descriptor registers, for the harnesses that strip hosted services by type.</summary>
public static class HostedServiceDescriptors
{
    /// <summary>
    /// The implementation type of a descriptor however it was registered. Hangfire's AddHangfireServer registers
    /// BackgroundJobServerHostedService through a factory, so ImplementationType is null there and a filter on it alone
    /// let the kit's PostgreSQL-configured server run beside the harness's in-memory one; the factory delegate's own
    /// type (Func&lt;IServiceProvider, T&gt;) still carries T, which is what the container itself reads.
    /// </summary>
    public static Type? ImplementationTypeOf(ServiceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.IsKeyedService)
        {
            return descriptor.KeyedImplementationType
                ?? descriptor.KeyedImplementationInstance?.GetType()
                ?? descriptor.KeyedImplementationFactory?.GetType().GenericTypeArguments[^1];
        }

        return descriptor.ImplementationType
            ?? descriptor.ImplementationInstance?.GetType()
            ?? descriptor.ImplementationFactory?.GetType().GenericTypeArguments[^1];
    }
}
