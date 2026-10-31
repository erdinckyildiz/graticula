using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-191 conditions 3, 4 and 5: a service definition Pro uploads is bounded before anything is written, only the
/// parts the ADR names are read, and one sent in several parts is put back together in order. The archives are 7z
/// files written by 7-Zip (<c>ServiceDefinitions/make_fixtures.py</c>), two of them with one entry renamed to a path
/// that leaves the archive.
/// </summary>
public sealed class ServiceDefinitionArchiveTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("graticula-sd-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "ServiceDefinitions", name);

    private string Zip => Path.Combine(_directory, "data.zip");

    [Fact]
    public async Task Only_the_named_parts_are_read_and_only_the_geodatabase_is_copied()
    {
        // Condition 4. The archive also holds a map document whose content is not JSON, a thumbnail, a file beside the
        // geodatabase and a file in a folder of its own: had any been opened as configuration the read would fail, and
        // had any been copied the zip would hold it.
        ServiceDefinition definition = await ServiceDefinitionArchive.ExtractAsync(
            Fixture("valid.sd"), Zip, CancellationToken.None);

        Assert.Equal("Fixture_Service", definition.ServiceName);
        Assert.Equal("Pro", definition.FolderName);
        Assert.Equal("Query,Create", definition.Capabilities);
        Assert.Equal(2000, definition.MaxRecordCount);
        Assert.Equal(1, definition.Layers);
        Assert.Equal("Roads", Assert.Single(definition.DrawingInfos).Key);
        Assert.Equal("A fixture", definition.Item.Snippet);
        Assert.Equal("a,b", definition.Item.Tags);

        using ZipArchive zip = ZipFile.OpenRead(Zip);
        Assert.Equal(
            ["data.gdb/a00000001.gdbtable", "data.gdb/a00000001.gdbtablx", "data.gdb/gdb"],
            zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData("climbs-out.sd", "../evl")]
    [InlineData("absolute.sd", "C:/evl")]
    public async Task An_entry_outside_the_archive_refuses_it_before_anything_is_written(string fixture, string entry)
    {
        // Condition 3. The bad entry sorts after the geodatabase in the archive, so a check made while copying would
        // already have written the data; the zip must not exist at all.
        InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ServiceDefinitionArchive.ExtractAsync(Fixture(fixture), Zip, CancellationToken.None));

        Assert.Contains(entry, refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Zip));
    }

    [Fact]
    public async Task Too_many_entries_refuse_the_archive_before_anything_is_written()
    {
        // Condition 3. valid.sd holds 18 entries, folders included; nine are allowed here.
        InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ServiceDefinitionArchive.ExtractAsync(Fixture("valid.sd"), Zip, 9, ServiceDefinitionArchive.MaximumBytes,
                CancellationToken.None));

        Assert.Contains("at most 9", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Zip));
    }

    [Fact]
    public async Task A_geodatabase_larger_than_the_bound_refuses_the_archive_before_anything_is_written()
    {
        // Condition 3. The geodatabase is 703 bytes uncompressed (500 + 200 + 3); 702 are allowed.
        InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ServiceDefinitionArchive.ExtractAsync(Fixture("valid.sd"), Zip, ServiceDefinitionArchive.MaximumMembers, 702,
                CancellationToken.None));

        Assert.Contains("703", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Zip));

        // And exactly at the bound it is read.
        await ServiceDefinitionArchive.ExtractAsync(
            Fixture("valid.sd"), Zip, ServiceDefinitionArchive.MaximumMembers, 703, CancellationToken.None);
        Assert.True(File.Exists(Zip));
    }

    [Fact]
    public async Task A_definition_sent_in_parts_out_of_order_is_reassembled_in_their_numbers_order()
    {
        // Condition 5. Eleven parts, so that part 10 and 11 would sort before 2 by an unpadded name, written in reverse
        // so that arrival order is not number order; the joined file is the archive, byte for byte, and it opens.
        byte[] whole = await File.ReadAllBytesAsync(Fixture("valid.sd"));
        int size = (whole.Length + 10) / 11;

        for (int number = 11; number >= 1; number--)
        {
            int from = (number - 1) * size;
            await File.WriteAllBytesAsync(
                PortalEndpoints.PartPath(_directory, number),
                whole.AsSpan(from, Math.Min(size, whole.Length - from)).ToArray());
        }

        string joined = Path.Combine(_directory, "joined.sd");
        await PortalEndpoints.ReassembleAsync(_directory, joined, CancellationToken.None);

        Assert.Equal(whole, await File.ReadAllBytesAsync(joined));
        Assert.Empty(Directory.GetFiles(_directory, "part-*"));
        Assert.Equal("Fixture_Service",
            (await ServiceDefinitionArchive.ExtractAsync(joined, Zip, CancellationToken.None)).ServiceName);
    }
}
