using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Coverages;
using Graticula.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// An image's overviews are built by the import reader's process, not the serving one — D-295.
/// </summary>
/// <remarks>
/// <b>The build is the one read that decodes every block of a file a stranger chose</b> (ADR-148's pass over a registered
/// image). These hold that the server's builder is the reader's, that it writes what the in-process builder wrote, and
/// that a file the child cannot read reaches the callers as the exception they already catch.
/// </remarks>
public sealed class OverviewsAreBuiltOutsideTheServingProcessTests
{
    private static ReaderPyramidBuilder Builder() =>
        new(new GeodatabaseReader(GeodatabaseReader.ExecutableBesideThisOne(), NullLogger<GeodatabaseReader>.Instance));

    [Fact]
    public async Task The_reader_writes_the_overviews_beside_the_image_and_says_how_many()
    {
        const int Width = 1000, Height = 700;
        double[] samples = new double[Width * Height];

        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = i % 251;
        }

        string path = Path.Combine(Path.GetTempPath(), $"graticula-reader-pyramid-{Guid.NewGuid():N}.tif");
        File.WriteAllBytes(path, GeoTiffWriter.Write(samples, Width, Height, 1, SampleKind.Real32, 30, 41, 0.001, 0.001, 4326, true, null));

        try
        {
            Assert.Equal(2, await Builder().BuildAsync(path, CancellationToken.None));
            Assert.True(File.Exists(path + ".ovr"), "The reader said it built overviews and none are beside the image.");

            // Built once: asked again, an image that has overviews is left alone.
            Assert.Equal(0, await Builder().BuildAsync(path, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".ovr");
        }
    }

    [Fact]
    public async Task A_file_the_reader_cannot_read_is_an_io_failure_the_callers_already_catch()
    {
        string path = Path.Combine(Path.GetTempPath(), $"graticula-reader-pyramid-{Guid.NewGuid():N}.tif");
        await File.WriteAllBytesAsync(path, [0x49, 0x49, 0x2A, 0x00, 0xFF, 0xFF, 0xFF, 0x7F, 1, 2, 3]);

        try
        {
            await Assert.ThrowsAsync<IOException>(() => Builder().BuildAsync(path, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_server_registers_the_readers_builder_and_not_the_in_process_one()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);

        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CLAUDE.md")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);

        string program = File.ReadAllText(Path.Combine(root!.FullName, "src", "Graticula.Host", "Program.cs"));

        Assert.Contains("AddSingleton<ICoveragePyramidBuilder, ReaderPyramidBuilder>()", program, StringComparison.Ordinal);
        Assert.DoesNotContain("TiffPyramidBuilder", program, StringComparison.Ordinal);
    }
}
