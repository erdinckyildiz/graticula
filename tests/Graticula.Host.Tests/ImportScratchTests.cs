using System;
using System.Collections.Generic;
using System.IO;
using Graticula.Host;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The scratch directory forgets an archive nobody acted on.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the other half of a decision made on 2026-08-19, and it is tested because that decision
/// created the failure it prevents.</b> An inspection used to release its archive the moment it
/// finished. [ADR-038](../../docs/adr/ADR-038-how-a-geodatabase-becomes-a-service.md) needed it kept —
/// the operator chooses which feature classes to publish <em>from what the inspection found</em>, so
/// releasing it would mean uploading two gigabytes again to act on the answer. The publish releases it.
/// Nobody releases it when nobody publishes, and <c>KeepAsync</c>'s budget would then refuse the next
/// upload with a message saying jobs are failing to clean up — which would be false, and would send an
/// operator looking for a fault that does not exist.
/// </para>
/// <para>
/// <b>Age is the only signal, and the test says so by exercising both sides of it.</b> A file whose job
/// finished is already gone; what is left is either being chosen from right now or abandoned, and no row
/// anywhere tells those apart. So the assertion that matters is not *the old one goes* on its own — it
/// is that the young one <em>stays</em>, because a sweep that took an archive out from under an open
/// selection screen would be worse than the leak it fixes.
/// </para>
/// </remarks>
public sealed class ImportScratchTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "graticula-scratch-" + Guid.NewGuid().ToString("N")[..8]);

    private ImportScratch Scratch() => new(
        HostSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Graticula:PlatformStore"] = "Host=localhost;Database=gis",
                ["Graticula:SecretKey"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                ["Graticula:ImportScratchPath"] = _directory,
            }).Build()),
        NullLogger<ImportScratch>.Instance);

    private string Write(string name, TimeSpan old)
    {
        Directory.CreateDirectory(_directory);

        string path = Path.Combine(_directory, name);

        File.WriteAllBytes(path, new byte[1024]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - old);

        return path;
    }

    [Fact]
    public void An_archive_nobody_acted_on_is_swept_and_a_fresh_one_is_not()
    {
        string abandoned = Write("abandoned.zip", ImportScratch.Patience + TimeSpan.FromMinutes(1));
        string chosenFrom = Write("beingchosenfrom.zip", TimeSpan.FromMinutes(3));

        int swept = Scratch().Sweep(ImportScratch.Patience);

        Assert.Equal(1, swept);

        Assert.False(
            File.Exists(abandoned),
            "An archive older than the patience survived the sweep, so a browser closed on the "
            + "selection screen holds its upload against the scratch budget for ever.");

        Assert.True(
            File.Exists(chosenFrom),
            "The sweep deleted an archive three minutes old. An operator reading a 55-layer selection "
            + "screen would have had the file removed underneath them, and the publish would refuse "
            + "with 'the archive is no longer on this server' for no reason they could see.");
    }

    /// <summary>
    /// A sweep of a directory that was never created is nothing, not an exception.
    /// </summary>
    /// <remarks>
    /// <b>Because it runs on a worker's idle tick.</b> The directory is created by the first upload, so
    /// on a server nobody has uploaded to, this is the case that runs every ten minutes for ever —
    /// and an exception there would be logged as a claim failure on a loop that is working perfectly.
    /// </remarks>
    [Fact]
    public void Sweeping_a_directory_that_does_not_exist_is_not_an_error()
    {
        Assert.Equal(0, Scratch().Sweep(TimeSpan.Zero));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory in the temp directory.
        }
    }
}
