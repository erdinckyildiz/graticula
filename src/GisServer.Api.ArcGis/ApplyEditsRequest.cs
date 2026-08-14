using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using GisServer.Catalog;
using GisServer.Features;
using GisServer.Geometries;

namespace GisServer.Api.ArcGis;

/// <summary>
/// Parses an ArcGIS <c>applyEdits</c> request into an <see cref="EditBatch"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-feature failures are collected, not thrown.</b> A batch of five
/// hundred features with one unreadable geometry must produce one failure and
/// four hundred and ninety-nine attempts — so a feature that cannot be parsed
/// becomes a pre-filled failed result rather than an exception that loses the
/// rest.
/// </para>
/// <para>
/// <b>Attribute values are converted using the column's real type</b>, taken
/// from the database rather than guessed from the JSON. A string that happens to
/// look like a number must not silently become one, and an ArcGIS date — which
/// is epoch milliseconds, not a string — must not be stored as a number.
/// </para>
/// </remarks>
public static class ApplyEditsRequest
{
    /// <summary>A feature that could not be parsed, and why.</summary>
    /// <param name="Index">Its position in the submitted array.</param>
    /// <param name="ObjectId">Its id if one was readable, else -1.</param>
    /// <param name="Error">What is wrong with it.</param>
    public readonly record struct Rejected(int Index, long ObjectId, string Error);

    /// <summary>What a parse produced.</summary>
    /// <param name="Batch">The features that parsed.</param>
    /// <param name="RejectedAdds">Adds that did not, with their positions.</param>
    /// <param name="RejectedUpdates">Updates that did not.</param>
    /// <param name="RejectedDeletes">Deletes that did not.</param>
    public sealed record Parsed(
        EditBatch Batch,
        IReadOnlyList<Rejected> RejectedAdds,
        IReadOnlyList<Rejected> RejectedUpdates,
        IReadOnlyList<Rejected> RejectedDeletes);

    /// <summary>Parses the three edit arrays.</summary>
    /// <param name="adds">The <c>adds</c> value, JSON, or null.</param>
    /// <param name="updates">The <c>updates</c> value, JSON, or null.</param>
    /// <param name="deletes">The <c>deletes</c> value: JSON array or comma-separated ids.</param>
    /// <param name="rollbackOnFailure">Whether one failure abandons the batch.</param>
    /// <param name="layer">The layer being edited.</param>
    /// <param name="fields">Its real columns.</param>
    /// <param name="error">Set when the request is malformed as a whole.</param>
    public static Parsed? TryParse(
        string? adds,
        string? updates,
        string? deletes,
        bool rollbackOnFailure,
        LayerDefinition layer,
        IReadOnlyList<FieldDescription> fields,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(fields);

        error = null;

        List<FeatureAdd> parsedAdds = [];
        List<FeatureUpdate> parsedUpdates = [];
        List<long> parsedDeletes = [];
        List<Rejected> rejectedAdds = [];
        List<Rejected> rejectedUpdates = [];
        List<Rejected> rejectedDeletes = [];

        if (!TryFeatures(adds, "adds", out JsonElement addArray, out error)
            || !TryFeatures(updates, "updates", out JsonElement updateArray, out error))
        {
            return null;
        }

        int index = 0;

        foreach (JsonElement feature in Enumerate(addArray))
        {
            if (TryFeature(feature, layer, fields, requireObjectId: false,
                    out long _, out Dictionary<string, object?>? attributes,
                    out Geometry? geometry, out string? why))
            {
                parsedAdds.Add(new FeatureAdd(attributes!, geometry));
            }
            else
            {
                rejectedAdds.Add(new Rejected(index, -1, why!));
            }

            index++;
        }

        index = 0;

        foreach (JsonElement feature in Enumerate(updateArray))
        {
            if (TryFeature(feature, layer, fields, requireObjectId: true,
                    out long objectId, out Dictionary<string, object?>? attributes,
                    out Geometry? geometry, out string? why))
            {
                parsedUpdates.Add(new FeatureUpdate(objectId, attributes!, geometry));
            }
            else
            {
                rejectedUpdates.Add(new Rejected(index, objectId, why!));
            }

            index++;
        }

        if (!TryDeletes(deletes, parsedDeletes, rejectedDeletes, out error))
        {
            return null;
        }

        return new Parsed(
            new EditBatch(
                parsedAdds,
                parsedUpdates,
                parsedDeletes,
                rollbackOnFailure,

                // Carried through so the writer's all-or-nothing covers features
                // it never received. Without it a batch with one unparseable
                // feature commits the rest and reports success.
                rejectedAdds.Count + rejectedUpdates.Count + rejectedDeletes.Count),
            rejectedAdds,
            rejectedUpdates,
            rejectedDeletes);
    }

    /// <summary>Enumerates an array, or nothing if it is absent.</summary>
    /// <remarks>
    /// Returns the struct enumerator rather than <c>IEnumerable</c> so that an
    /// empty batch does not allocate — this runs once per edit request, and a
    /// default <c>ArrayEnumerator</c> over a default element yields nothing.
    /// </remarks>
    private static JsonElement.ArrayEnumerator Enumerate(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : default;

    private static bool TryFeatures(
        string? value, string name, out JsonElement array, out string? error)
    {
        array = default;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        try
        {
            JsonElement parsed = JsonDocument.Parse(value).RootElement.Clone();

            if (parsed.ValueKind != JsonValueKind.Array)
            {
                error = $"'{name}' must be a JSON array of features.";
                return false;
            }

            array = parsed;
            return true;
        }
        catch (JsonException e)
        {
            error = $"'{name}' is not valid JSON: {e.Message}";
            return false;
        }
    }

    /// <summary>
    /// Reads the deletes, which ArcGIS sends two different ways.
    /// </summary>
    /// <remarks>
    /// A JSON array from newer clients, a comma-separated list from older ones.
    /// Both are accepted because both are what real clients send; the
    /// alternative is a compatibility surface that works for half of them.
    /// </remarks>
    private static bool TryDeletes(
        string? value, List<long> into, List<Rejected> rejected, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        string trimmed = value.Trim();

        if (trimmed.StartsWith('['))
        {
            try
            {
                int index = 0;

                foreach (JsonElement id in JsonDocument.Parse(trimmed).RootElement.EnumerateArray())
                {
                    if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out long value64))
                    {
                        into.Add(value64);
                    }
                    else
                    {
                        rejected.Add(new Rejected(index, -1, "A delete must be an integer object id."));
                    }

                    index++;
                }

                return true;
            }
            catch (JsonException e)
            {
                error = $"'deletes' is not valid JSON: {e.Message}";
                return false;
            }
        }

        int position = 0;

        foreach (string part in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
            {
                into.Add(id);
            }
            else
            {
                rejected.Add(new Rejected(position, -1, $"'{part}' is not an integer object id."));
            }

            position++;
        }

        return true;
    }

    private static bool TryFeature(
        JsonElement feature,
        LayerDefinition layer,
        IReadOnlyList<FieldDescription> fields,
        bool requireObjectId,
        out long objectId,
        out Dictionary<string, object?>? attributes,
        out Geometry? geometry,
        out string? error)
    {
        objectId = -1;
        attributes = null;
        geometry = null;
        error = null;

        if (feature.ValueKind != JsonValueKind.Object)
        {
            error = "A feature must be a JSON object.";
            return false;
        }

        Dictionary<string, object?> values = new(StringComparer.Ordinal);

        if (feature.TryGetProperty("attributes", out JsonElement bag)
            && bag.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in bag.EnumerateObject())
            {
                if (string.Equals(property.Name, layer.ObjectIdColumn, StringComparison.Ordinal))
                {
                    if (property.Value.ValueKind == JsonValueKind.Number
                        && property.Value.TryGetInt64(out long id))
                    {
                        objectId = id;
                    }

                    continue;
                }

                if (!TryConvert(property.Value, property.Name, fields, out object? converted, out error))
                {
                    return false;
                }

                values[property.Name] = converted;
            }
        }

        if (requireObjectId && objectId < 0)
        {
            error =
                $"An update must carry '{layer.ObjectIdColumn}' in its attributes so the server "
                + "knows which feature to change.";
            return false;
        }

        if (feature.TryGetProperty("geometry", out JsonElement shape)
            && shape.ValueKind != JsonValueKind.Null)
        {
            if (!ArcGisGeometryReader.TryRead(shape, layer.Srid, out geometry, out error))
            {
                return false;
            }
        }

        attributes = values;
        return true;
    }

    /// <summary>
    /// Converts one JSON value to the column's real type.
    /// </summary>
    /// <remarks>
    /// <b>Driven by the column, not by the JSON.</b> Letting the JSON decide
    /// means a quoted number silently becomes text in a numeric column — or
    /// worse, that an ArcGIS date, which is epoch milliseconds, lands in a
    /// timestamp column as a very large number of years.
    /// </remarks>
    private static bool TryConvert(
        JsonElement value,
        string column,
        IReadOnlyList<FieldDescription> fields,
        out object? converted,
        out string? error)
    {
        converted = null;
        error = null;

        FieldType? type = null;

        foreach (FieldDescription field in fields)
        {
            if (string.Equals(field.Name, column, StringComparison.Ordinal))
            {
                type = field.Type;
                break;
            }
        }

        if (type is null)
        {
            // Left for the writer's whitelist to refuse, with its better
            // message. Converting an unknown column would be deciding it is
            // acceptable.
            converted = null;
            error = $"'{column}' is not a column of this layer.";
            return false;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        try
        {
            converted = type switch
            {
                FieldType.SmallInteger => (short)value.GetInt32(),
                FieldType.Integer => value.GetInt32(),
                FieldType.BigInteger => value.GetInt64(),
                FieldType.Single => (float)value.GetDouble(),
                FieldType.Double => value.GetDouble(),
                FieldType.Boolean => ReadBoolean(value),
                FieldType.Guid => Guid.Parse(value.GetString()!),

                // ArcGIS carries a date as epoch milliseconds, UTC. A client
                // sending a string here is not speaking ArcGIS, and guessing a
                // format would put the wrong day in the database.
                FieldType.Date => DateTimeOffset.FromUnixTimeMilliseconds(value.GetInt64()).UtcDateTime,

                FieldType.Binary => Convert.FromBase64String(value.GetString()!),
                _ => value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : value.GetRawText(),
            };

            return true;
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or OverflowException)
        {
            error =
                $"'{column}' is {type} and the value sent for it cannot be read as one: "
                + $"{value.GetRawText()}";
            return false;
        }
    }

    private static bool ReadBoolean(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,

        // ArcGIS writes a boolean out as 1 or 0, so it reads one back too.
        JsonValueKind.Number => value.GetInt32() != 0,
        _ => throw new FormatException("not a boolean"),
    };
}
