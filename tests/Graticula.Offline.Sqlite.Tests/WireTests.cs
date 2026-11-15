using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis.Offline;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Offline.Sqlite.Tests;

/// <summary>
/// What the offline reader's child sends the host survives the trip unchanged, and a malformed answer is refused —
/// ADR-288 condition 7.
/// </summary>
public sealed class WireTests
{
    [Fact]
    public async Task A_delta_read_from_a_file_crosses_the_wire_unchanged()
    {
        string path = OfflineFixture.NewPath("delta.geodatabase");
        await new SqliteMobileGeodatabaseWriter().WriteDeltaAsync(DeltaTests.Delta(), path, CancellationToken.None);
        OfflineDelta read = await new SqliteOfflineDeltaReader().ReadDeltaAsync(path, OfflineFixture.Layers, CancellationToken.None);

        IReadOnlyList<OfflineLayer> layers = OfflineFixture.Layers;
        OfflineDelta crossed = Cross(read, layers);

        AssertSame(read, crossed);
        Assert.All(crossed.Layers, c => Assert.Same(layers.Single(l => l.Id == c.Layer.Id), c.Layer));
    }

    [Fact]
    public void Every_value_type_a_row_holds_crosses_the_wire()
    {
        OfflineLayer layer = Wide();
        object?[] values =
        [
            7L, 8, (short)9, 1.5, "naïve – text", new DateTimeOffset(2026, 11, 10, 8, 30, 0, TimeSpan.Zero), Guid.NewGuid(),
            new byte[] { 0, 1, 255 }, null,
        ];
        Geometry line = new LineString(XySequence.Wrap([0.5, 1.5, 2.5, 3.5]));
        OfflineDelta delta = new(Guid.NewGuid(), 42, [new OfflineLayerChanges(
            layer,
            [new OfflineRow(values, Point.Create(1, 2, 3, 4)), new OfflineRow(values, null)],
            [new OfflineRow(values, line)],
            [new OfflineKey(5, Guid.NewGuid())],
            [new OfflineAttachment(1, Guid.NewGuid(), Guid.NewGuid(), "photo.jpg", "image/jpeg", new byte[100_000])],
            [],
            [new OfflineKey(2, Guid.NewGuid())])], "svc");

        OfflineDelta crossed = Cross(delta, [layer]);

        AssertSame(delta, crossed);
        Point p = Assert.IsAssignableFrom<Point>(crossed.Layers[0].Inserts[0].Geometry);
        Assert.Equal((1d, 2d, 3d, 4d), (p.X, p.Y, p.Z!.Value, p.M!.Value));
        Assert.Equal("svc", crossed.ServiceName);
    }

    [Fact]
    public void A_value_no_row_holds_is_refused_by_the_writer()
    {
        OfflineLayer layer = Wide();
        object?[] values = [7L, 8, (short)9, 1.5, "x", DateTimeOffset.UnixEpoch, Guid.Empty, Array.Empty<byte>(), new object()];
        OfflineDelta delta = new(Guid.Empty, 0, [new OfflineLayerChanges(layer, [new OfflineRow(values, null)], [], [], [], [], [])], "");

        Assert.Throws<NotSupportedException>(() => OfflineDeltaWire.Write(delta, new MemoryStream()));
    }

    [Fact]
    public void Shapes_cross_the_wire_with_what_the_read_needs()
    {
        JsonElement json = JsonDocument.Parse(OfflineDeltaWire.Shapes(OfflineFixture.Layers).ToJsonString()).RootElement;
        IReadOnlyList<OfflineLayer> shapes = OfflineDeltaWire.ReadShapes(json);

        Assert.Equal(OfflineFixture.Layers.Count, shapes.Count);

        foreach ((OfflineLayer sent, OfflineLayer got) in OfflineFixture.Layers.Zip(shapes))
        {
            Assert.Equal(
                (sent.Id, sent.Name, sent.GeometryType, sent.Ordinates, sent.Wkid, sent.Geographic, sent.ReferenceWkt, sent.ObjectIdField, sent.GlobalIdField, sent.HasAttachments),
                (got.Id, got.Name, got.GeometryType, got.Ordinates, got.Wkid, got.Geographic, got.ReferenceWkt, got.ObjectIdField, got.GlobalIdField, got.HasAttachments));
            Assert.Equal(
                sent.Fields.Select(static f => (f.Name, f.EsriType, f.Alias, f.Length, f.Nullable)),
                got.Fields.Select(static f => (f.Name, f.EsriType, f.Alias, f.Length, f.Nullable)));
        }
    }

    [Fact]
    public void An_answer_naming_a_layer_that_was_not_sent_is_refused()
    {
        byte[] bytes = Bytes(DeltaTests.Delta());

        InvalidDataException refused = Assert.Throws<InvalidDataException>(
            () => OfflineDeltaWire.Read(new MemoryStream(bytes), [OfflineFixture.LinesLayer]));
        Assert.Contains("not asked about", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_answer_is_refused()
    {
        byte[] bytes = Bytes(DeltaTests.Delta());

        for (int cut = 0; cut < bytes.Length; cut += 7)
        {
            Assert.Throws<InvalidDataException>(
                () => OfflineDeltaWire.Read(new MemoryStream(bytes[..cut]), OfflineFixture.Layers));
        }
    }

    [Fact]
    public void A_count_longer_than_the_answer_is_refused_before_anything_is_allocated()
    {
        byte[] bytes = Bytes(new OfflineDelta(Guid.Empty, 0, [], ""));

        // The layer count is the last four bytes of an empty delta.
        BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, bytes.Length - 4);

        InvalidDataException refused = Assert.Throws<InvalidDataException>(
            () => OfflineDeltaWire.Read(new MemoryStream(bytes), OfflineFixture.Layers));
        Assert.Contains("claims", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_answer_of_another_version_or_none_is_refused()
    {
        byte[] bytes = Bytes(new OfflineDelta(Guid.Empty, 0, [], ""));
        bytes[8] = OfflineDeltaWire.Version + 1;

        Assert.Throws<InvalidDataException>(() => OfflineDeltaWire.Read(new MemoryStream(bytes), OfflineFixture.Layers));
        Assert.Throws<InvalidDataException>(() => OfflineDeltaWire.Read(new MemoryStream("not a delta at all"u8.ToArray()), OfflineFixture.Layers));
    }

    internal static OfflineDelta Cross(OfflineDelta delta, IReadOnlyList<OfflineLayer> layers)
    {
        using MemoryStream stream = new(Bytes(delta));
        return OfflineDeltaWire.Read(stream, layers);
    }

    private static byte[] Bytes(OfflineDelta delta)
    {
        using MemoryStream stream = new();
        OfflineDeltaWire.Write(delta, stream);
        return stream.ToArray();
    }

    internal static void AssertSame(OfflineDelta expected, OfflineDelta actual)
    {
        Assert.Equal(expected.ReplicaId, actual.ReplicaId);
        Assert.Equal(expected.ServerGeneration, actual.ServerGeneration);
        Assert.Equal(expected.Layers.Select(static l => l.Layer.Id), actual.Layers.Select(static l => l.Layer.Id));

        foreach ((OfflineLayerChanges e, OfflineLayerChanges a) in expected.Layers.Zip(actual.Layers))
        {
            AssertRows(e.Inserts, a.Inserts);
            AssertRows(e.Updates, a.Updates);
            Assert.Equal(e.Deletes, a.Deletes);
            AssertAttachments(e.AttachmentInserts, a.AttachmentInserts);
            AssertAttachments(e.AttachmentUpdates, a.AttachmentUpdates);
            Assert.Equal(e.AttachmentDeletes, a.AttachmentDeletes);
        }
    }

    private static void AssertRows(IReadOnlyList<OfflineRow> expected, IReadOnlyList<OfflineRow> actual)
    {
        Assert.Equal(expected.Count, actual.Count);

        foreach ((OfflineRow e, OfflineRow a) in expected.Zip(actual))
        {
            Assert.Equal(e.Values, a.Values);
            Assert.Equal(
                e.Geometry is null ? null : WkbWriter.ToArray(e.Geometry),
                a.Geometry is null ? null : WkbWriter.ToArray(a.Geometry));
        }
    }

    private static void AssertAttachments(IReadOnlyList<OfflineAttachment> expected, IReadOnlyList<OfflineAttachment> actual)
    {
        Assert.Equal(
            expected.Select(static x => (x.AttachmentId, x.ParentGlobalId, x.GlobalId, x.Name, x.ContentType, Convert.ToBase64String(x.Data.Span))),
            actual.Select(static x => (x.AttachmentId, x.ParentGlobalId, x.GlobalId, x.Name, x.ContentType, Convert.ToBase64String(x.Data.Span))));
    }

    private static OfflineLayer Wide()
    {
        string[] types =
        [
            "esriFieldTypeOID", "esriFieldTypeInteger", "esriFieldTypeSmallInteger", "esriFieldTypeDouble", "esriFieldTypeString",
            "esriFieldTypeDate", "esriFieldTypeGUID", "esriFieldTypeBlob", "esriFieldTypeString",
        ];
        OfflineField[] fields = [.. types.Select((t, i) => new OfflineField("f" + i, t, null, null, true, null))];
        return OfflineFixture.PointsLayer with { Id = 9, Fields = fields };
    }
}
