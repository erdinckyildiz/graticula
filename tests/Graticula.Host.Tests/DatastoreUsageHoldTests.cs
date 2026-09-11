using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// That the datastore's size is measured at most once a minute, and says when — D-237.
/// </summary>
/// <remarks>
/// <b>Why it is held at all</b>: Operations samples `/admin/health` every five seconds and the
/// statement behind this costs 70–125 ms on the development store, growing with the number of
/// hosted tables. These pin the three things the hold promises: it reuses, it expires, and two
/// callers who arrive together cause one measurement.
/// </remarks>
public sealed class DatastoreUsageHoldTests
{
    private static readonly DatastoreUsage Some = new(1_000_000, 4_096, []);

    [Fact]
    public async Task A_reading_is_reused_until_it_is_a_minute_old_and_then_taken_again()
    {
        Clock clock = new();
        DatastoreUsageHold hold = new(clock);
        int measured = 0;

        Task<DatastoreUsage> Measure(CancellationToken _)
        {
            measured++;
            return Task.FromResult(Some with { HostedBytes = measured });
        }

        DatastoreUsageHold.Reading first = await hold.ReadAsync(Measure, CancellationToken.None);

        clock.Now += TimeSpan.FromSeconds(59);
        DatastoreUsageHold.Reading second = await hold.ReadAsync(Measure, CancellationToken.None);

        Assert.Equal(1, measured);
        Assert.Same(first, second);

        clock.Now += TimeSpan.FromSeconds(2);
        DatastoreUsageHold.Reading third = await hold.ReadAsync(Measure, CancellationToken.None);

        Assert.Equal(2, measured);
        Assert.Equal(2, third.Usage.HostedBytes);

        // <b>The reading carries when it was taken</b>, which is what lets the report say how old
        // it is instead of passing a held figure off as a live one.
        Assert.Equal(clock.Now, third.At);
    }

    [Fact]
    public async Task Callers_who_arrive_together_cause_one_measurement()
    {
        DatastoreUsageHold hold = new(new Clock());
        TaskCompletionSource<DatastoreUsage> slow = new();
        int measured = 0;

        Task<DatastoreUsage> Measure(CancellationToken _)
        {
            Interlocked.Increment(ref measured);
            return slow.Task;
        }

        Task<DatastoreUsageHold.Reading>[] asking =
            Enumerable.Range(0, 8).Select(_ => hold.ReadAsync(Measure, CancellationToken.None)).ToArray();

        slow.SetResult(Some);

        DatastoreUsageHold.Reading[] answers = await Task.WhenAll(asking);

        Assert.Equal(1, measured);
        Assert.All(answers, a => Assert.Same(answers[0], a));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
