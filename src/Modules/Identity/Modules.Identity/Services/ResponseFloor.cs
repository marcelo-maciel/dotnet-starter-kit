using System.Diagnostics;

namespace FSH.Modules.Identity.Services;

/// <summary>
/// Holds every answer of the routes that take an e-mail without a tenant to one minimum duration (M2, M3), so the
/// time of the answer does not tell an unknown e-mail from a wrong password, a locked account or an address that
/// just got a reset link. The floor sits well above the slowest of those paths (see <see cref="FloorMilliseconds"/>).
/// </summary>
// ponytail: fixed floor, from one measurement on the dev machine. A host several times slower, or one under load,
// can push a path past it and the difference shows again; the upgrade path is an adaptive floor fed by the
// observed p99 of these routes.
internal static class ResponseFloor
{
    /// <summary>
    /// 400 ms. Measured on 04/11/2026 against the integration host (TestServer, PostgreSQL in a container, four
    /// rounds after a warm-up): unknown e-mail 57-77 ms, wrong password 69-85 ms, forgot password for a known
    /// e-mail 12-29 ms against 3-6 ms for an unknown one. The floor is about five times the slowest failure, for a
    /// production host slower than the dev machine and for load.
    /// </summary>
    public const int FloorMilliseconds = 400;

    public static readonly TimeSpan Floor = TimeSpan.FromMilliseconds(FloorMilliseconds);

    /// <summary>Runs <paramref name="work"/> and, whether it returns or throws, answers no sooner than the floor.</summary>
    public static async Task<T> AtLeastAsync<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var start = Stopwatch.GetTimestamp();
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            // Not the request's token: a cancelled delay would replace the real outcome with a cancellation. A loop,
            // because the timer works in whole milliseconds and can wake a fraction of one early.
            for (var remaining = Floor - Stopwatch.GetElapsedTime(start);
                 remaining > TimeSpan.Zero;
                 remaining = Floor - Stopwatch.GetElapsedTime(start))
            {
                await Task.Delay(remaining + TimeSpan.FromMilliseconds(1), CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
