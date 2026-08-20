using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Formats;
using Graticula.Geometries;

namespace Graticula.Api.Wfs;

/// <summary>
/// Writes a feature collection as GeoJSON.
/// </summary>
/// <remarks>
/// <para>
/// <b>Always WGS 84, because that is what GeoJSON is.</b> RFC 7946 removed the
/// <c>crs</c> member and pins coordinates to WGS 84 longitude/latitude. This
/// repository has already taken that position once, on the other side of the
/// wire: the import path refuses a GeoJSON file whose features are not in 4326,
/// and ADR-038 §4B records that the guard was right when it fired. Writing a
/// national grid's easting and northing into a GeoJSON document would be the same
/// error with the arrow reversed — nothing would report it, and every reader
/// would place the features in the Gulf of Guinea.
/// </para>
/// <para>
/// So the host reprojects to 4326 for this format. A request that asks for
/// GeoJSON <em>and</em> another reference is refused rather than served in one of
/// the two, which is the only honest answer when the two cannot both be true.
/// </para>
/// <para>
/// <b>Longitude first, always.</b> GeoJSON has no axis-order question — the
/// specification fixes the order — so none of <see cref="WfsNames.IsLatitudeFirst"/>
/// applies here. That asymmetry with the GML writer is the specification's, not
/// ours.
/// </para>
/// </remarks>
public sealed class GeoJsonFeatureCollectionWriter
{
    /// <summary>The only reference GeoJSON is written in.</summary>
    /// <summary>
    /// The reference system this face writes in.
    /// </summary>
    /// <remarks>
    /// <b>RFC 7946's, and it is not this surface's to choose.</b> Kept as a member
    /// here because WFS callers ask this type what reference a GeoJSON response is
    /// in; the value comes from <see cref="GeoJsonWriter.Srid"/>, which is the one
    /// place that decides it now that two faces write the format.
    /// </remarks>
    public const int Srid = GeoJsonWriter.Srid;

    private readonly WfsFeatureType _type;

    /// <summary>Creates a writer for one feature type.</summary>
    /// <param name="type">The type being written.</param>
    public GeoJsonFeatureCollectionWriter(WfsFeatureType type)
    {
        ArgumentNullException.ThrowIfNull(type);

        _type = type;
    }

    /// <summary>Writes the collection.</summary>
    /// <param name="stream">Where to write it.</param>
    /// <param name="features">The page of features, read as they are written.</param>
    /// <param name="numberMatched">How many match, or null for unknown.</param>
    /// <param name="numberReturned">How many this page holds.</param>
    /// <param name="timestamp">When the response was produced.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task WriteAsync(
        Stream stream,
        IAsyncEnumerable<Feature> features,
        long? numberMatched,
        long numberReturned,
        DateTimeOffset timestamp,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(features);

        Utf8JsonWriter json = new(stream);

        await using (json.ConfigureAwait(false))
        {
            json.WriteStartObject();
            json.WriteString("type", "FeatureCollection");

            json.WriteString(
                "timeStamp",
                timestamp.UtcDateTime.ToString(
                    "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

            if (numberMatched is { } matched)
            {
                json.WriteNumber("numberMatched", matched);
            }
            else
            {
                json.WriteString("numberMatched", "unknown");
            }

            json.WriteNumber("numberReturned", numberReturned);

            json.WriteStartArray("features");

            await foreach (Feature feature in features.WithCancellation(cancellation)
                .ConfigureAwait(false))
            {
                Write(json, feature);

                // Flushed per feature so a large page leaves the process in
                // pieces rather than as one buffer. The same reasoning as the
                // ArcGIS writer: A-037 makes the peak the thing to bound.
                if (json.BytesPending > 16 * 1024)
                {
                    await json.FlushAsync(cancellation).ConfigureAwait(false);
                }
            }

            json.WriteEndArray();
            json.WriteEndObject();

            await json.FlushAsync(cancellation).ConfigureAwait(false);
        }
    }

    private void Write(Utf8JsonWriter json, Feature feature)
    {
        json.WriteStartObject();
        json.WriteString("type", "Feature");
        json.WriteString("id", _type.GmlIdOf(feature.Id));

        json.WritePropertyName("geometry");

        if (feature.Geometry is { } geometry)
        {
            GeoJsonWriter.WriteGeometry(json, geometry);
        }
        else
        {
            json.WriteNullValue();
        }

        json.WriteStartObject("properties");

        for (int i = 0; i < feature.Schema.Count; i++)
        {
            json.WritePropertyName(feature.Schema.Names[i]);
            GeoJsonWriter.WriteValue(json, feature[i]);
        }

        json.WriteEndObject();
        json.WriteEndObject();
    }

}
