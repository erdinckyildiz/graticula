using System;
using System.Text.Json;
using System.Threading.Tasks;
using Graticula.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The geodatabase reader has a bound on the machine, and not only a bound on time.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-94](../../docs/architecture-debt.md) stated the gap in its own words: <i>what it does not
/// provide is a CPU or memory bound, and that is the part written down here</i>.</b> The deadline
/// and <c>DOTNET_GCHeapHardLimit</c> were already tested — see
/// <see cref="GeodatabaseReaderTests"/> — and neither is what the row was about. A two-minute
/// deadline permits two minutes of holding the whole machine, and the heap limit does not reach
/// the allocation GDAL actually makes.
/// </para>
/// <para>
/// <b>Both bounds are the parent's, so both are tested from the parent.</b> The child is the part
/// that is parsing a file somebody else chose; a limit it applies to itself is a limit an
/// adversary is asked to respect. What it does do is <i>report</i> — <c>ping</c> answers with the
/// priority it is running at — because a setting nothing can observe is a setting that quietly
/// stops being applied.
/// </para>
/// </remarks>
/// <remarks>
/// <b>This class needs a machine whose process timing is predictable —
/// [ADR-048](../../docs/adr/ADR-048-ci-does-not-run-the-real-data-suites.md) §5b.</b>
/// The memory guard polls, and its first line is <c>if (process.HasExited) return</c>:
/// catching a child past its ceiling requires the child to still be alive when the
/// guard task is first scheduled. On a contended runner that is a coin flip. It was
/// tried twice — first with a `ping` the child answers in milliseconds, then with a
/// `drivers` call that loads GDAL — and CI called tails on both, so the honest
/// classification is that the test needs a quiet machine rather than a longer child.
/// </remarks>
[Trait("Needs", "QuietMachine")]
public sealed class ReaderResourceBoundTests
{
    private static GeodatabaseReader Reader() =>
        new(GeodatabaseReader.ExecutableBesideThisOne(), NullLogger<GeodatabaseReader>.Instance);

    /// <summary>
    /// The reader runs below the server in the scheduler's order.
    /// </summary>
    /// <remarks>
    /// <b>Asked of the child rather than asserted of the code.</b> The alternative was to read
    /// <c>PriorityClass</c> back off a process the reader owns and disposes, which tests that the
    /// property was set and not that the process is running that way. This answer comes from
    /// inside the child, after it started, which is the only place the question has an answer.
    /// <para>
    /// <b>Verified by removing <c>Yield</c>: the answer becomes <c>Normal</c> and this fails.</b>
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_reader_runs_below_normal_so_serving_wins_the_machine()
    {
        using JsonDocument answer = await Reader()
            .AskAsync(new { op = "ping" }, TimeSpan.FromSeconds(30));

        string priority = answer.RootElement.GetProperty("priority").GetString() ?? string.Empty;

        Assert.Equal("BelowNormal", priority);
    }

    /// <summary>
    /// A child past its memory ceiling is killed, and the refusal says memory rather than time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A ceiling a healthy child exceeds, because the alternative is a test nobody runs.</b>
    /// Two gigabytes is the real bound and no archive in this repository approaches it; waiting
    /// for one that does would mean writing an adversarial geodatabase first. One megabyte is
    /// below what a .NET process holds the moment it starts, so the guard's first sample is past
    /// it — which exercises the poll, the cancellation and the message, all of which are the parts
    /// that can be wrong.
    /// </para>
    /// <para>
    /// <b>The assertion is that it does not say <i>deadline</i>.</b> Both bounds arrive as the same
    /// cancellation, and a caller told only that something stopped the read cannot tell an archive
    /// too big for the machine from an archive that is slow. Those have different answers, so the
    /// message is the repair as much as the kill is.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_reader_past_its_memory_ceiling_is_killed_and_the_message_names_memory()
    {
        GeodatabaseReader tight = new(
            GeodatabaseReader.ExecutableBesideThisOne(),
            NullLogger<GeodatabaseReader>.Instance,
            1L << 20);

        // <b>`drivers`, not `ping`, and the difference is a race — 2026-08-25.</b>
        // The guard samples immediately and then every 250 ms, but its first line is
        // `if (process.HasExited) return;`: a child that answers before the guard task
        // is scheduled cannot be caught. `ping` answers in single-digit milliseconds, so
        // this was a coin flip that came up heads on every developer machine and on the
        // first CI run, and tails on the second.
        //
        // `drivers` loads GDAL and enumerates 296 of them, which takes hundreds of
        // milliseconds — long enough for the first sample to land while the child is
        // alive, and far past a one-megabyte ceiling either way.
        InvalidOperationException killed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tight.AskAsync(new { op = "drivers" }, TimeSpan.FromSeconds(30)));

        Assert.Contains("MB", killed.Message, StringComparison.Ordinal);
        Assert.Contains("killed", killed.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(
            "deadline",
            killed.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The ceiling that ships is not one a healthy read comes anywhere near.
    /// </summary>
    /// <remarks>
    /// <b>The other half of a guard: that it does not fire.</b> A bound tested only by tripping it
    /// is a bound that could be set to zero and still pass. <c>ping</c> loads GDAL's native
    /// payload, which is the largest allocation this child makes before it has read anything, and
    /// it answers.
    /// </remarks>
    [Fact]
    public async Task A_healthy_read_stays_under_the_ceiling_that_ships()
    {
        using JsonDocument answer = await Reader()
            .AskAsync(new { op = "ping" }, TimeSpan.FromSeconds(30));

        Assert.True(answer.RootElement.GetProperty("ok").GetBoolean());
    }
}
