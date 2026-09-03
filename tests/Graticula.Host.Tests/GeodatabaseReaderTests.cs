using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Graticula.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The server can start the geodatabase reader, and GDAL loads inside it and not here.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is under test is the spawn, not the reading.</b> The reader itself was measured against
/// three real geodatabases before any of this existed — see
/// [file-geodatabase-readers.md](../../docs/research/file-geodatabase-readers.md) §8d and §8e — so
/// what has never been exercised is the part this server owns: that the executable is where the build
/// puts it, that a JSON line goes in and one comes out, that a deadline kills, and that a refusal
/// arrives as an answer rather than as a dead process.
/// </para>
/// <para>
/// <b>And it is the check the build plumbing needs.</b> The overlay worker's own history is the
/// warning: copying the executable without its dependency closure left every overlay answering *could
/// not load file or assembly*, from inside a process where the host could only report it as an invalid
/// request. This reader's closure is much larger — GDAL's native payloads for two platforms — so a
/// test that merely asserts the file exists would miss the interesting half. <c>ping</c> asserts the
/// drivers, which means the native libraries actually loaded.
/// </para>
/// </remarks>
public sealed class GeodatabaseReaderTests
{
    private static GeodatabaseReader Reader() =>
        new(GeodatabaseReader.ExecutableBesideThisOne(), NullLogger<GeodatabaseReader>.Instance);

    [Fact]
    public void The_reader_is_installed_beside_the_test_assembly()
    {
        Assert.True(
            Reader().Available,
            $"The reader is not at '{GeodatabaseReader.ExecutableBesideThisOne()}'. It is copied there "
            + "by the CopyImportReader target, which this test project opts into — the same "
            + "arrangement the overlay worker uses, and for the same reason: whoever runs a child "
            + "process needs its whole dependency closure in a directory beside them.");
    }

    /// <summary>
    /// Ping answers, names a GDAL version, and reports the two drivers this design depends on.
    /// </summary>
    /// <remarks>
    /// <b>The drivers are the assertion, not the version string.</b> <c>OpenFileGDB</c> is what reads a
    /// geodatabase and <c>Parquet</c> is what the conversion writes — Q-74's boundary, chosen because
    /// GeoParquet carries the schema and the CRS with the data. A build whose native payload is missing
    /// still answers <c>ping</c>; it answers with both of these false, which is the failure this asserts
    /// against.
    /// </remarks>
    [Fact]
    public async Task Ping_answers_with_a_version_and_the_two_drivers_this_design_needs()
    {
        using JsonDocument answer = await Reader()
            .AskAsync(new { op = "ping" }, TimeSpan.FromSeconds(30));

        JsonElement root = answer.RootElement;

        Assert.True(root.GetProperty("ok").GetBoolean());

        string version = root.GetProperty("gdal").GetString() ?? string.Empty;

        Assert.False(
            string.IsNullOrWhiteSpace(version),
            "The reader started and answered without a GDAL version, which means GdalBase.ConfigureAll "
            + "found nothing to configure.");

        JsonElement drivers = root.GetProperty("drivers");

        Assert.True(
            drivers.GetProperty("openFileGdb").GetBoolean(),
            $"GDAL {version} loaded without the OpenFileGDB driver, so no geodatabase can be read. "
            + "The native payload is incomplete rather than absent — an absent one would not have "
            + "answered at all.");

        Assert.True(
            drivers.GetProperty("parquet").GetBoolean(),
            $"GDAL {version} loaded without the Parquet driver, so a layer can be listed and not "
            + "converted. Q-74 chose GeoParquet as the boundary between the reader and the importer.");
    }

    /// <summary>
    /// An operation the reader does not have is an answer, not a dead process.
    /// </summary>
    /// <remarks>
    /// <b>Because the host has a job row to write a reason into.</b> A child that died silently would
    /// leave it saying only *failed*, which <c>IJobStore</c> refuses precisely because nobody can act
    /// on it. The reader's own loop catches everything and answers; this is the check that it does.
    /// </remarks>
    [Fact]
    public async Task An_operation_that_does_not_exist_comes_back_as_a_refusal()
    {
        using JsonDocument answer = await Reader()
            .AskAsync(new { op = "sing" }, TimeSpan.FromSeconds(30));

        Assert.False(answer.RootElement.GetProperty("ok").GetBoolean());

        string error = answer.RootElement.GetProperty("error").GetString() ?? string.Empty;

        // It names what it does answer, because a refusal that only says *no* sends the reader to the
        // source to find out what the operations are.
        Assert.Contains("layers", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("convert", error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The reader answers exactly four operations, and its own refusal names all four.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is [ADR-037](../../docs/adr/ADR-037-job-workers-come-in-two-kinds.md) condition 3, and it
    /// was written after the fourth operation arrived.</b> The condition says the set must stay closed:
    /// a worker that reads untrusted files is a worker whose surface has to be enumerable, and a fifth
    /// operation added to <c>Run</c> is a fifth thing an archive can reach. ADR-038 added
    /// <c>features</c> — which is exactly the event the condition anticipated, and nothing failed when
    /// it did.
    /// </para>
    /// <para>
    /// <b>Two halves, because either alone is weak.</b> The refusal's own sentence is parsed for the
    /// names it lists, which fails when an operation is added and the sentence is not updated; and every
    /// name it lists is then asked for, which fails when the sentence lists something the reader does not
    /// answer. A test that only asserted a hard-coded list would need editing in the same commit as the
    /// change it is supposed to catch.
    /// </para>
    /// <para>
    /// <b>What *asked for* means here, and why it is not *succeeds*.</b> Three of the four need an
    /// archive that does not exist in a test, so the assertion is that each is refused *for its own
    /// reason* rather than as an unknown operation. An operation that exists and an operation that does
    /// not give different answers, and that difference is the whole check.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_reader_answers_exactly_the_operations_its_refusal_names()
    {
        using JsonDocument refused = await Reader()
            .AskAsync(new { op = "sing" }, TimeSpan.FromSeconds(30));

        string sentence = refused.RootElement.GetProperty("error").GetString() ?? string.Empty;

        // Every 'quoted' name in the refusal, minus the operation that was asked for.
        HashSet<string> named = new(StringComparer.Ordinal);

        foreach (System.Text.RegularExpressions.Match quoted in
                 System.Text.RegularExpressions.Regex.Matches(sentence, "'([a-z]+)'"))
        {
            named.Add(quoted.Groups[1].Value);
        }

        named.Remove("sing");

        // <b>`fixture` joined the list on 2026-09-03</b>, when Q-138 was answered by generating
        // a geodatabase at test time rather than committing one. It is named here because this
        // test's whole point is that the refusal and the switch cannot drift apart, and the
        // first change after it was written did exactly that.
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "ping", "layers", "convert", "features", "fixture",
            },
            named);

        foreach (string operation in named)
        {
            using JsonDocument answer = await Reader().AskAsync(
                new { op = operation, archive = "C:/nothing/is/here/absent.gdb.zip", layer = "any" },
                TimeSpan.FromSeconds(30));

            JsonElement root = answer.RootElement;

            if (root.GetProperty("ok").GetBoolean())
            {
                continue;
            }

            string why = root.GetProperty("error").GetString() ?? string.Empty;

            Assert.DoesNotContain(
                "is not an operation", why, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// An archive that is not there is a refusal with a reason in it.
    /// </summary>
    [Fact]
    public async Task An_archive_that_is_not_there_is_refused_by_the_reader()
    {
        using JsonDocument answer = await Reader().AskAsync(
            new { op = "layers", archive = "C:/nothing/is/here/absent.gdb.zip" },
            TimeSpan.FromSeconds(30));

        Assert.False(answer.RootElement.GetProperty("ok").GetBoolean());

        Assert.False(
            string.IsNullOrWhiteSpace(answer.RootElement.GetProperty("error").GetString()),
            "The reader refused an absent archive without saying anything, which is the one thing a "
            + "job row cannot be written from.");
    }

    /// <summary>
    /// The deadline kills the child and says so in a sentence somebody can act on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Falsified rather than assumed, which is this repository's habit for a guard.</b> A zero
    /// deadline is the cheapest way to force the path: it cancels before the child can answer anything.
    /// </para>
    /// <para>
    /// <b>What it proves and what it does not.</b> It proves the cancellation is wired to a kill and
    /// that the message names the bound rather than leaking an <c>OperationCanceledException</c> — the
    /// same defect this repository fixed once already, where a broken guard reported *exception while
    /// reading from stream* after two minutes instead of saying what it was waiting for. It does not
    /// prove that a genuinely slow archive is interrupted mid-read; nothing short of an adversarial
    /// geodatabase does, and none exists yet.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_deadline_that_has_already_passed_kills_the_reader_and_says_why()
    {
        InvalidOperationException killed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Reader().AskAsync(new { op = "ping" }, TimeSpan.Zero));

        Assert.Contains("deadline", killed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("killed", killed.Message, StringComparison.OrdinalIgnoreCase);
    }
}
