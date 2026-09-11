using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;

namespace Graticula.Host;

/// <summary>
/// The datastore's size and who is filling it, measured at most once a minute — D-237.
/// </summary>
/// <remarks>
/// <para>
/// <b>Held, because the Operations screen asks every five seconds and the answer moves in
/// minutes.</b> The statement costs 37–81 ms on the development store plus 35–45 ms for the
/// database's own size (measured 2026-09-11), and it grows with the number of hosted tables. At
/// one sample per five seconds that is a query running against PostgreSQL for as long as
/// somebody leaves a tab open, to learn a number that changes when somebody uploads.
/// </para>
/// <para>
/// <b>The time it was measured travels with it</b>, so the report says how old it is rather
/// than letting a held figure pass for a live one — ADR-010 §6b's rule that cache state is
/// readable, applied to the one thing here that is a cache.
/// </para>
/// <para>
/// <b>One measurement at a time.</b> Two tabs opening Operations together would otherwise
/// both find the reading stale and both run the statement; the second waits and takes the
/// first one's answer.
/// </para>
/// </remarks>
public sealed class DatastoreUsageHold : IDisposable
{
    /// <summary>How long a reading is reused.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly TimeProvider _clock;

    // A reference, so a reader outside the lock sees a whole reading or the previous one and
    // never half of each — which a two-field struct written under the lock could not promise.
    private volatile Reading? _last;

    /// <summary>Creates the hold on the system clock.</summary>
    public DatastoreUsageHold()
        : this(TimeProvider.System)
    {
    }

    /// <summary>Creates the hold on a given clock, which is how a test moves time.</summary>
    /// <param name="clock">The clock.</param>
    public DatastoreUsageHold(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        _clock = clock;
    }

    /// <summary>The most recent reading, measuring again when it is older than <see cref="Hold"/>.</summary>
    /// <param name="measure">
    /// How to measure — <see cref="IAdminCatalog.DatastoreUsageAsync"/> in the server. A
    /// function rather than the catalogue, so the hold can be tested without a catalogue's
    /// forty other members standing behind it.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The reading and when it was taken.</returns>
    public async Task<Reading> ReadAsync(
        Func<CancellationToken, Task<DatastoreUsage>> measure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(measure);

        if (Fresh(_last) is { } held)
        {
            return held;
        }

        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (Fresh(_last) is { } meanwhile)
            {
                return meanwhile;
            }

            DatastoreUsage usage = await measure(cancellationToken).ConfigureAwait(false);

            Reading taken = new(usage, _clock.GetUtcNow());
            _last = taken;

            return taken;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _one.Dispose();

    private Reading? Fresh(Reading? reading) =>
        reading is not null && _clock.GetUtcNow() - reading.At < Hold ? reading : null;

    /// <summary>One measurement.</summary>
    /// <param name="Usage">What it found.</param>
    /// <param name="At">When.</param>
    public sealed record Reading(DatastoreUsage Usage, DateTimeOffset At);
}
