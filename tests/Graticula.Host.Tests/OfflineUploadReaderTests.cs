using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis.Offline;
using Graticula.Geometries;
using Graticula.Host;
using Graticula.Offline.Sqlite;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A client's upload is read in the offline reader's process, and every way that can fail reaches the client as a
/// sentence — ADR-288 condition 7.
/// </summary>
/// <remarks>
/// <b>Against the real child</b>, copied beside this assembly by <c>CopyOfflineReader</c>, for the overlay worker
/// tests' reason: the bounds being tested are a process being stopped, and a fake would test the bookkeeping.
/// </remarks>
public sealed class OfflineUploadReaderTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-4222-8222-222222222222");

    [Fact]
    public async Task An_upload_is_read_by_the_child_and_names_the_layers_the_host_sent()
    {
        OfflineLayer layer = OfflineReplicaTests.Shape();
        OfflineDelta written = new(Guid.NewGuid(), 1794288415192,
        [
            new OfflineLayerChanges(
                layer,
                [OfflineReplicaTests.Row(1, A, "added offline")],
                [OfflineReplicaTests.Row(2, B, "edited offline")],
                [new OfflineKey(3, Guid.NewGuid())],
                [new OfflineAttachment(4, A, Guid.NewGuid(), "photo.jpg", "image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })],
                [],
                []),
        ], "Hydrants");

        string path = NewPath();
        await new SqliteMobileGeodatabaseWriter().WriteDeltaAsync(written, path, CancellationToken.None);

        OfflineDelta read = await Reader().ReadDeltaAsync(path, [layer], CancellationToken.None);

        OfflineLayerChanges changes = Assert.Single(read.Layers);
        Assert.Same(layer, changes.Layer);
        Assert.Equal(written.ReplicaId, read.ReplicaId);
        Assert.Equal(written.Layers[0].Inserts[0].Values, Assert.Single(changes.Inserts).Values);
        Point at = Assert.IsAssignableFrom<Point>(changes.Inserts[0].Geometry);
        Assert.Equal(5, at.Z);
        Assert.Equal("edited offline", Assert.Single(changes.Updates).Values[3]);
        Assert.Equal(3L, Assert.Single(changes.Deletes).ObjectId);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, Assert.Single(changes.AttachmentInserts).Data.ToArray());
    }

    [Fact]
    public async Task A_file_that_is_not_a_geodatabase_is_refused_with_the_childs_reason()
    {
        string path = NewPath();
        await File.WriteAllTextAsync(path, "this is not a SQLite file, and it is long enough to have a header's worth of bytes in it.");

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Reader().ReadDeltaAsync(path, [OfflineReplicaTests.Shape()], CancellationToken.None));

        Assert.Contains("SqliteException", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_upload_is_refused_rather_than_read_as_empty()
    {
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Reader().ReadDeltaAsync(NewPath(), [OfflineReplicaTests.Shape()], CancellationToken.None));

        Assert.Contains("FileNotFoundException", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_without_the_child_says_so()
    {
        OfflineUploadReader reader = new(
            Path.Combine(Path.GetTempPath(), "nowhere", "Graticula.Offline.Reader.exe"), Scratch(), TimeSpan.FromMinutes(1), 1L << 30);

        Assert.False(reader.Available);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadDeltaAsync(NewPath(), [OfflineReplicaTests.Shape()], CancellationToken.None));

        Assert.Contains("not installed", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_child_past_its_deadline_is_stopped()
    {
        OfflineUploadReader reader = new(
            OfflineUploadReader.ExecutableBesideThisOne(), Scratch(), TimeSpan.FromMilliseconds(1), 1L << 30);

        string written = await WrittenAsync();

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadDeltaAsync(written, [OfflineReplicaTests.Shape()], CancellationToken.None));

        Assert.Contains("took longer than", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_child_past_its_memory_ceiling_is_stopped()
    {
        // A megabyte, which a .NET process exceeds before it reads its first line: the guard is what is being tested.
        OfflineUploadReader reader = new(
            OfflineUploadReader.ExecutableBesideThisOne(), Scratch(), TimeSpan.FromMinutes(1), 1L << 20);

        string written = await WrittenAsync();

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadDeltaAsync(written, [OfflineReplicaTests.Shape()], CancellationToken.None));

        Assert.Contains("this server allows one upload", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_answer_file_is_deleted_after_a_read()
    {
        string scratch = Scratch();
        OfflineUploadReader reader = new(OfflineUploadReader.ExecutableBesideThisOne(), scratch, TimeSpan.FromMinutes(1), 1L << 30);

        await reader.ReadDeltaAsync(await WrittenAsync(), [OfflineReplicaTests.Shape()], CancellationToken.None);

        Assert.Empty(Directory.GetFiles(scratch));
    }

    [Theory]
    [InlineData("""{"ok":true}""", null)]
    [InlineData("""{"ok":false,"error":"SqliteException: file is not a database"}""", "SqliteException: file is not a database")]
    [InlineData("""{"ok":false}""", "it gave no reason.")]
    [InlineData("""[1,2]""", "it gave no reason.")]
    [InlineData("""Unhandled exception.""", "is not JSON")]
    public void The_childs_answer_is_read_for_its_refusal(string answer, string? said)
    {
        if (said is null)
        {
            OfflineUploadReader.Refusal(answer);
            return;
        }

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => OfflineUploadReader.Refusal(answer));
        Assert.Contains(said, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// What Field Maps 26.2.2 uploaded (research note §5b), through the real child, when <c>GRATICULA_OFFLINE_UPLOAD</c>
    /// names the file. It stays outside the repository.
    /// </summary>
    [Fact]
    public async Task An_upload_Field_Maps_wrote_is_read_through_the_child()
    {
        string? path = Environment.GetEnvironmentVariable("GRATICULA_OFFLINE_UPLOAD");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        OfflineLayer probe = new(
            0, "zz_offline_probe", string.Empty, "esriGeometryPoint", GeometryOrdinates.Z, 4326, true, string.Empty,
            [
                new("objectid", "esriFieldTypeOID", null, null, false, null),
                new("note", "esriFieldTypeString", null, 100, true, null),
                new("size", "esriFieldTypeDouble", null, null, true, null),
                new("globalid", "esriFieldTypeGlobalID", null, 38, false, null),
            ],
            "objectid", "globalid", true, null, None<OfflineRow>, None<OfflineAttachment>);

        OfflineDelta read = await Reader().ReadDeltaAsync(path, [probe], CancellationToken.None);

        OfflineLayerChanges changes = Assert.Single(read.Layers);
        Assert.Equal("offline3", Assert.Single(changes.Inserts).Values[1]);
        Assert.Equal(15, Assert.IsAssignableFrom<Point>(changes.Inserts[0].Geometry).Z);
        Assert.Equal("offline1", Assert.Single(changes.Updates).Values[1]);
        Assert.Equal(4L, Assert.Single(changes.Deletes).ObjectId);
    }

    private static OfflineUploadReader Reader() =>
        new(OfflineUploadReader.ExecutableBesideThisOne(), Scratch(), TimeSpan.FromMinutes(1), OfflineUploadReader.MemoryCeilingBytes);

    private static async Task<string> WrittenAsync()
    {
        string path = NewPath();
        await new SqliteMobileGeodatabaseWriter().WriteDeltaAsync(
            new OfflineDelta(Guid.NewGuid(), 1, [new OfflineLayerChanges(OfflineReplicaTests.Shape(), [OfflineReplicaTests.Row(1, A, "x")], [], [], [], [], [])], "Hydrants"),
            path,
            CancellationToken.None);
        return path;
    }

    private static string NewPath()
    {
        string folder = Path.Combine(Path.GetTempPath(), "graticula-offline-reader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "upload.geodatabase");
    }

    private static string Scratch() =>
        Path.Combine(Path.GetTempPath(), "graticula-offline-reader-scratch", Guid.NewGuid().ToString("N"));

    private static async IAsyncEnumerable<T> None<T>([EnumeratorCancellation] CancellationToken cancellation = default)
    {
        await Task.CompletedTask;
        cancellation.ThrowIfCancellationRequested();
        yield break;
    }
}
