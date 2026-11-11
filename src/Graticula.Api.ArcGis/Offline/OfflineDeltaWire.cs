using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;

namespace Graticula.Api.ArcGis.Offline;

/// <summary>
/// What the host and the offline delta reader pass each other — ADR-288 condition 7.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two halves, in two forms.</b> The host sends the layers' shapes as JSON on the child's standard input, beside
/// the paths, because they are small and a person reading a failure wants to see them. The child sends the changes back
/// in a file, in a binary form, because they carry attachments: a photo is bytes, and base64 in a JSON line would add a
/// third to it and hold the whole line in memory twice. The file is a temporary one the host names and deletes.
/// </para>
/// <para>
/// <b>Geometry as ISO WKB with its Z and M</b>, through <see cref="WkbWriter"/> and <see cref="WkbReader"/>, not as the
/// mobile geodatabase's own blob. The host should not need to decode the client's format at all; the child decodes it
/// and hands over this server's geometry model in a form this server already reads.
/// </para>
/// <para>
/// <b>The host reads this file defensively.</b> The child is this server's own code, but it has just parsed a hostile
/// file with native code, so its output is not trusted further than its format: every count and length is checked
/// against what is left in the file before anything is allocated, and a layer id the host did not send is refused.
/// </para>
/// <para>
/// <b>What a shape leaves out:</b> the layer's REST document, its extent and its fields' domains. The read uses none of
/// them — it matches tables by layer id and name and converts values by field type — and a domain's coded values are
/// typed objects that JSON would have to be told how to carry.
/// </para>
/// </remarks>
public static class OfflineDeltaWire
{
    /// <summary>The version of the binary form; a reader refuses any other.</summary>
    public const byte Version = 1;

    private static readonly byte[] Magic = "GRTDELTA"u8.ToArray();

    private enum Tag : byte
    {
        Null = 0,
        Int64 = 1,
        Int32 = 2,
        Int16 = 3,
        Double = 4,
        String = 5,
        DateTimeOffset = 6,
        Guid = 7,
        Bytes = 8,
    }

    // ------------------------------------------------------------------------------------------------ shapes

    /// <summary>The layers' shapes, as the JSON the child is sent.</summary>
    /// <param name="layers">The replica's layers.</param>
    /// <returns>An array, one object per layer.</returns>
    public static JsonArray Shapes(IReadOnlyList<OfflineLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);

        JsonArray array = [];

        foreach (OfflineLayer layer in layers)
        {
            JsonArray fields = [];

            foreach (OfflineField field in layer.Fields)
            {
                fields.Add(new JsonObject
                {
                    ["name"] = field.Name,
                    ["type"] = field.EsriType,
                    ["alias"] = field.Alias,
                    ["length"] = field.Length,
                    ["nullable"] = field.Nullable,
                });
            }

            array.Add(new JsonObject
            {
                ["id"] = layer.Id,
                ["name"] = layer.Name,
                ["geometryType"] = layer.GeometryType,
                ["ordinates"] = (int)layer.Ordinates,
                ["wkid"] = layer.Wkid,
                ["geographic"] = layer.Geographic,
                ["wkt"] = layer.ReferenceWkt,
                ["objectIdField"] = layer.ObjectIdField,
                ["globalIdField"] = layer.GlobalIdField,
                ["hasAttachments"] = layer.HasAttachments,
                ["fields"] = fields,
            });
        }

        return array;
    }

    /// <summary>Reads the shapes <see cref="Shapes"/> wrote, as layers with nothing to stream.</summary>
    /// <param name="array">The array.</param>
    /// <returns>The layers.</returns>
    /// <exception cref="InvalidDataException">The array is not one <see cref="Shapes"/> wrote.</exception>
    public static IReadOnlyList<OfflineLayer> ReadShapes(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The layers are not an array.");
        }

        List<OfflineLayer> layers = [];

        try
        {
            foreach (JsonElement layer in array.EnumerateArray())
            {
                List<OfflineField> fields = [];

                foreach (JsonElement field in layer.GetProperty("fields").EnumerateArray())
                {
                    fields.Add(new OfflineField(
                        field.GetProperty("name").GetString()!,
                        field.GetProperty("type").GetString()!,
                        Text(field, "alias"),
                        field.TryGetProperty("length", out JsonElement length) && length.ValueKind == JsonValueKind.Number ? length.GetInt32() : null,
                        field.GetProperty("nullable").GetBoolean(),
                        null));
                }

                layers.Add(new OfflineLayer(
                    layer.GetProperty("id").GetInt32(),
                    layer.GetProperty("name").GetString()!,
                    string.Empty,
                    Text(layer, "geometryType"),
                    (GeometryOrdinates)layer.GetProperty("ordinates").GetInt32(),
                    layer.GetProperty("wkid").GetInt32(),
                    layer.GetProperty("geographic").GetBoolean(),
                    Text(layer, "wkt") ?? string.Empty,
                    fields,
                    layer.GetProperty("objectIdField").GetString()!,
                    Text(layer, "globalIdField"),
                    layer.GetProperty("hasAttachments").GetBoolean(),
                    null,
                    None<OfflineRow>,
                    None<OfflineAttachment>));
            }
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"The layers are not in the form the host sends: {e.Message}", e);
        }

        return layers;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async IAsyncEnumerable<T> None<T>([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    // ------------------------------------------------------------------------------------------------ changes

    /// <summary>Writes the changes in the binary form.</summary>
    /// <param name="delta">The changes the child read.</param>
    /// <param name="stream">Where to write them.</param>
    /// <exception cref="NotSupportedException">A value is of a type a row may not hold.</exception>
    public static void Write(OfflineDelta delta, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(delta);
        ArgumentNullException.ThrowIfNull(stream);

        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(delta.ReplicaId.ToByteArray());
        writer.Write(delta.ServerGeneration);
        WriteText(writer, delta.ServiceName);
        writer.Write(delta.Layers.Count);

        foreach (OfflineLayerChanges changes in delta.Layers)
        {
            writer.Write(changes.Layer.Id);
            WriteRows(writer, changes.Inserts);
            WriteRows(writer, changes.Updates);
            WriteKeys(writer, changes.Deletes);
            WriteAttachments(writer, changes.AttachmentInserts);
            WriteAttachments(writer, changes.AttachmentUpdates);
            WriteKeys(writer, changes.AttachmentDeletes);
        }

        writer.Flush();
    }

    /// <summary>Reads the changes <see cref="Write"/> wrote, naming the layers the caller holds.</summary>
    /// <param name="stream">A seekable stream at the start of the changes.</param>
    /// <param name="layers">The layers the request was sent with; each change names one of these instances.</param>
    /// <returns>The changes.</returns>
    /// <exception cref="InvalidDataException">The stream is not in the binary form, or names a layer not sent.</exception>
    public static OfflineDelta Read(Stream stream, IReadOnlyList<OfflineLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(layers);

        if (!stream.CanSeek)
        {
            throw new ArgumentException("The stream has to be seekable, so lengths can be checked against it.", nameof(stream));
        }

        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

        try
        {
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
            {
                throw new InvalidDataException("The reader's answer does not begin with its signature.");
            }

            if (reader.ReadByte() is var version && version != Version)
            {
                throw new InvalidDataException(
                    $"The reader answered in version {version} of its form and this server reads version {Version}; the two were built apart.");
            }

            Guid replica = ReadGuid(reader);
            long generation = reader.ReadInt64();
            string service = ReadText(reader);
            int count = Count(reader);

            Dictionary<int, OfflineLayer> byId = layers.ToDictionary(static l => l.Id);
            List<OfflineLayerChanges> changes = [];

            for (int i = 0; i < count; i++)
            {
                int id = reader.ReadInt32();

                if (!byId.TryGetValue(id, out OfflineLayer? layer))
                {
                    throw new InvalidDataException($"The reader answered for layer {id}, which it was not asked about.");
                }

                changes.Add(new OfflineLayerChanges(
                    layer,
                    ReadRows(reader, layer),
                    ReadRows(reader, layer),
                    ReadKeys(reader),
                    ReadAttachments(reader),
                    ReadAttachments(reader),
                    ReadKeys(reader)));
            }

            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException("The reader's answer has bytes after its last layer.");
            }

            return new OfflineDelta(replica, generation, changes, service);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("The reader's answer ends early.", e);
        }
    }

    private static void WriteRows(BinaryWriter writer, IReadOnlyList<OfflineRow> rows)
    {
        writer.Write(rows.Count);

        foreach (OfflineRow row in rows)
        {
            writer.Write(row.Values.Count);

            foreach (object? value in row.Values)
            {
                WriteValue(writer, value);
            }

            if (row.Geometry is null)
            {
                writer.Write(false);
            }
            else
            {
                writer.Write(true);
                WriteBytes(writer, WkbWriter.ToArray(row.Geometry));
            }
        }
    }

    private static List<OfflineRow> ReadRows(BinaryReader reader, OfflineLayer layer)
    {
        int count = Count(reader);
        List<OfflineRow> rows = new(count);

        for (int i = 0; i < count; i++)
        {
            int width = Count(reader);

            if (width != layer.Fields.Count)
            {
                throw new InvalidDataException(
                    $"A row of layer {layer.Id} has {width} values and the layer {layer.Fields.Count} fields.");
            }

            object?[] values = new object?[width];

            for (int v = 0; v < width; v++)
            {
                values[v] = ReadValue(reader);
            }

            Geometry? geometry = null;

            if (reader.ReadBoolean())
            {
                byte[] wkb = ReadBytes(reader);

                try
                {
                    geometry = WkbReader.Read(wkb, GeometryOrdinates.Z | GeometryOrdinates.M, out _);
                }
                catch (WkbFormatException e)
                {
                    throw new InvalidDataException($"A geometry of layer {layer.Id} is not readable: {e.Message}", e);
                }
            }

            rows.Add(new OfflineRow(values, geometry));
        }

        return rows;
    }

    private static void WriteKeys(BinaryWriter writer, IReadOnlyList<OfflineKey> keys)
    {
        writer.Write(keys.Count);

        foreach (OfflineKey key in keys)
        {
            writer.Write(key.ObjectId);
            writer.Write(key.GlobalId.ToByteArray());
        }
    }

    private static List<OfflineKey> ReadKeys(BinaryReader reader)
    {
        int count = Count(reader);
        List<OfflineKey> keys = new(count);

        for (int i = 0; i < count; i++)
        {
            keys.Add(new OfflineKey(reader.ReadInt64(), ReadGuid(reader)));
        }

        return keys;
    }

    private static void WriteAttachments(BinaryWriter writer, IReadOnlyList<OfflineAttachment> attachments)
    {
        writer.Write(attachments.Count);

        foreach (OfflineAttachment a in attachments)
        {
            writer.Write(a.AttachmentId);
            writer.Write(a.ParentGlobalId.ToByteArray());
            writer.Write(a.GlobalId.ToByteArray());
            WriteText(writer, a.Name);
            WriteText(writer, a.ContentType);
            writer.Write(a.Data.Length);
            writer.Write(a.Data.Span);
        }
    }

    private static List<OfflineAttachment> ReadAttachments(BinaryReader reader)
    {
        int count = Count(reader);
        List<OfflineAttachment> attachments = new(count);

        for (int i = 0; i < count; i++)
        {
            attachments.Add(new OfflineAttachment(
                reader.ReadInt64(),
                ReadGuid(reader),
                ReadGuid(reader),
                ReadText(reader),
                ReadText(reader),
                ReadBytes(reader)));
        }

        return attachments;
    }

    private static void WriteValue(BinaryWriter writer, object? value)
    {
        switch (value)
        {
            case null:
            case DBNull:
                writer.Write((byte)Tag.Null);
                break;
            case long l:
                writer.Write((byte)Tag.Int64);
                writer.Write(l);
                break;
            case int i:
                writer.Write((byte)Tag.Int32);
                writer.Write(i);
                break;
            case short s:
                writer.Write((byte)Tag.Int16);
                writer.Write(s);
                break;
            case double d:
                writer.Write((byte)Tag.Double);
                writer.Write(d);
                break;
            case float f:
                writer.Write((byte)Tag.Double);
                writer.Write((double)f);
                break;
            case string text:
                writer.Write((byte)Tag.String);
                WriteText(writer, text);
                break;
            case DateTimeOffset at:
                writer.Write((byte)Tag.DateTimeOffset);
                writer.Write(at.UtcTicks);
                writer.Write((short)at.Offset.TotalMinutes);
                break;
            case Guid g:
                writer.Write((byte)Tag.Guid);
                writer.Write(g.ToByteArray());
                break;
            case byte[] bytes:
                writer.Write((byte)Tag.Bytes);
                WriteBytes(writer, bytes);
                break;
            default:
                throw new NotSupportedException(
                    $"A row value of type {value.GetType().Name} is not one an offline row holds (OfflineRow lists them).");
        }
    }

    private static object? ReadValue(BinaryReader reader)
    {
        byte tag = reader.ReadByte();

        switch ((Tag)tag)
        {
            case Tag.Null:
                return null;
            case Tag.Int64:
                return reader.ReadInt64();
            case Tag.Int32:
                return reader.ReadInt32();
            case Tag.Int16:
                return reader.ReadInt16();
            case Tag.Double:
                return reader.ReadDouble();
            case Tag.String:
                return ReadText(reader);
            case Tag.DateTimeOffset:
                long ticks = reader.ReadInt64();
                short minutes = reader.ReadInt16();

                try
                {
                    TimeSpan offset = TimeSpan.FromMinutes(minutes);
                    return new DateTimeOffset(ticks, TimeSpan.Zero).ToOffset(offset);
                }
                catch (ArgumentOutOfRangeException e)
                {
                    throw new InvalidDataException("A date in the reader's answer is out of range.", e);
                }

            case Tag.Guid:
                return ReadGuid(reader);
            case Tag.Bytes:
                return ReadBytes(reader);
            default:
                throw new InvalidDataException(
                    string.Create(CultureInfo.InvariantCulture, $"A value in the reader's answer has the unknown tag {tag}."));
        }
    }

    private static void WriteText(BinaryWriter writer, string text) => WriteBytes(writer, Encoding.UTF8.GetBytes(text));

    private static string ReadText(BinaryReader reader) => Encoding.UTF8.GetString(ReadBytes(reader));

    private static void WriteBytes(BinaryWriter writer, byte[] bytes)
    {
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static Guid ReadGuid(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(16);

        return bytes.Length == 16 ? new Guid(bytes) : throw new EndOfStreamException();
    }

    /// <summary>A length, refused when it is negative or longer than what is left.</summary>
    private static byte[] ReadBytes(BinaryReader reader)
    {
        int length = Count(reader);
        byte[] bytes = reader.ReadBytes(length);

        return bytes.Length == length ? bytes : throw new EndOfStreamException();
    }

    /// <summary>
    /// A count or a length. Every element takes at least one byte, so a count larger than the bytes left is a lie,
    /// and refusing it here is what stops a hostile answer from asking for a list of two billion before it fails.
    /// </summary>
    private static int Count(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        long left = reader.BaseStream.Length - reader.BaseStream.Position;

        return count >= 0 && count <= left
            ? count
            : throw new InvalidDataException(
                string.Create(CultureInfo.InvariantCulture, $"The reader's answer claims {count} items with {left} bytes left."));
    }
}
