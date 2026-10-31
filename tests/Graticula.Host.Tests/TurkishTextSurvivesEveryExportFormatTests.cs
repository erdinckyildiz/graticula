using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Host;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// Turkish letters in field names and in values leave in every one of the eight export formats and come back intact —
/// ADR-106 condition 3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Through the export's own code, not a copy of it.</b> The rows are staged by <see cref="LayerExportRows"/> exactly as
/// <see cref="FeatureExporter"/> stages them, the reader child writes each format with the options the job sends, a
/// Shapefile's names are put right by <see cref="ShapefileFieldNames"/>, and the Feature Collection is written by the
/// query writer the job uses. Each file is then read back by GDAL in the same reader — a second, independent parser for
/// every format the reader wrote, since GDAL's read and write paths of a driver are separate code.
/// </para>
/// <para>
/// <b>Every letter Turkish adds to the Latin alphabet</b> — <c>ç ğ ı İ ö ş ü</c> and their capitals — is in a field
/// name somewhere and in a value. The dotless <c>ı</c> and the dotted <c>İ</c> are the two a case-folding or a code-page
/// conversion loses first: <c>İ</c> lowercases to <c>i̇</c> (two characters) under an invariant culture, and CP1252 has no
/// <c>ğ ş ı İ</c> at all. The 2026-10-07 measurement this replaces was a script run once by hand.
/// </para>
/// </remarks>
public sealed class TurkishTextSurvivesEveryExportFormatTests
{
    /// <summary>The fields after the identity, every Turkish letter among their names.</summary>
    private static readonly string[] Names = ["şehir", "ilçe_adı", "dağ_adı", "İl_no", "nüfus", "ÖĞÜŞ"];

    /// <summary>Three rows; the last carries every letter, lower and upper, in one value.</summary>
    private static readonly object?[][] Values =
    [
        ["İstanbul", "Üsküdar", "Çamlıca", 34, 15_655_924, "İĞNE"],
        ["Kırşehir", "Çiçekdağı", "Kervansaray Dağı", 40, 244_519, "ışık"],
        ["Iğdır", "Şırnak ğüşİıöç", "ÇĞIİÖŞÜ çğıiöşü", 76, 203_594, "Ağrı Dağı"],
    ];

    private static readonly LayerDefinition Definition =
        new("turkce", "public", "turkce", "geom", 4326, "objectid", "objectid", isHosted: false);

    private static readonly PublishedLayer Layer = new(
        Guid.NewGuid(), Definition, "postgis", "unused", GeometryKind.Point, owner: null, SharingScope.Public,
        ServiceStatus.Started);

    private static readonly LayerDescription Described = new(
        [
            new FieldDescription("objectid", FieldType.Integer, false, null),
            new FieldDescription(Names[0], FieldType.Text, true, 64),
            new FieldDescription(Names[1], FieldType.Text, true, 64),
            new FieldDescription(Names[2], FieldType.Text, true, 64),
            new FieldDescription(Names[3], FieldType.Integer, true, null),
            new FieldDescription(Names[4], FieldType.Integer, true, null),
            new FieldDescription(Names[5], FieldType.Text, true, 64),
        ],
        null);

    private static GeodatabaseReader Reader() =>
        new(GeodatabaseReader.ExecutableBesideThisOne(), NullLogger<GeodatabaseReader>.Instance);

    /// <summary>
    /// Each of the seven formats the reader writes, read back by GDAL with every name and every value as it left.
    /// </summary>
    [Theory]
    [InlineData(FeatureExportFormat.GeoPackage)]
    [InlineData(FeatureExportFormat.Shapefile)]
    [InlineData(FeatureExportFormat.Excel)]
    [InlineData(FeatureExportFormat.FileGeodatabase)]
    [InlineData(FeatureExportFormat.Kml)]
    [InlineData(FeatureExportFormat.Csv)]
    [InlineData(FeatureExportFormat.GeoJson)]
    public async Task A_format_the_reader_writes_reads_back_with_every_Turkish_letter(FeatureExportFormat format)
    {
        string folder = Path.Combine(Path.GetTempPath(), "tr-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            string staged = Path.Combine(folder, "in-0.geojson");

            await using (FileStream file = File.Create(staged))
            {
                long rows = await LayerExportRows.WriteGeoJsonAsync(
                    file, new TurkishRows(), Described, Layer, 4326, 100, FeatureQuery.MaximumLimit, null,
                    CancellationToken.None);

                Assert.Equal(Values.Length, rows);
            }

            // Where the job writes each format, and what it opens afterwards.
            string output = format switch
            {
                FeatureExportFormat.GeoPackage => Path.Combine(folder, "dataset.gpkg"),
                FeatureExportFormat.Excel => Path.Combine(folder, "dataset.xlsx"),
                FeatureExportFormat.Kml => Path.Combine(folder, "dataset.kml"),
                FeatureExportFormat.FileGeodatabase => Path.Combine(folder, "gdb", "export.gdb"),
                FeatureExportFormat.Shapefile => Path.Combine(folder, "shp", "turkce"),
                FeatureExportFormat.Csv => Path.Combine(folder, "turkce.csv"),
                _ => Path.Combine(folder, "turkce.geojson"),
            };

            Directory.CreateDirectory(Path.GetDirectoryName(output)!);

            using (JsonDocument wrote = await Reader().AskAsync(
                new
                {
                    op = "export",
                    @in = staged,
                    @out = output,
                    format = FeatureExportPackaging.ReaderToken(format),
                    layer = "turkce",
                    append = false,
                    domains = (object[]?)null,
                },
                TimeSpan.FromSeconds(60)))
            {
                Assert.True(wrote.RootElement.GetProperty("ok").GetBoolean(), $"{format}: {wrote.RootElement}");
            }

            if (format == FeatureExportFormat.Shapefile)
            {
                Assert.True(
                    ShapefileFieldNames.Apply(output, "turkce", [.. Described.Fields.Select(f => f.Name)]),
                    "The Shapefile's .dbf did not hold the fields the layer has, so its names were left as GDAL wrote them.");
            }

            (List<string> fields, List<Dictionary<string, string?>> rowsRead) = await ReadBackAsync(output, "turkce");

            // The Shapefile's names within ten bytes: these six fit, so each is the layer's own.
            foreach (string name in Names)
            {
                Assert.True(
                    fields.Contains(name, StringComparer.Ordinal),
                    $"{format}: the field '{name}' did not come back. GDAL read: {string.Join(", ", fields)}");
            }

            Assert.Equal(Values.Length, rowsRead.Count);

            for (int r = 0; r < Values.Length; r++)
            {
                Dictionary<string, string?> row = rowsRead.Single(
                    x => x[Names[3]] == Convert.ToString(Values[r][3], CultureInfo.InvariantCulture));

                for (int c = 0; c < Names.Length; c++)
                {
                    Assert.Equal(Convert.ToString(Values[r][c], CultureInfo.InvariantCulture), row[Names[c]]);
                }
            }
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The eighth, the Feature Collection, written by the host's query writer and read back by a JSON parser.
    /// </summary>
    /// <remarks>
    /// The writer escapes every non-ASCII letter (<c>ş</c>), which is valid JSON and what <c>query?f=json</c> sends;
    /// what matters is that a parser gets the letter back, and the field list and the attributes are both checked.
    /// </remarks>
    [Fact]
    public async Task The_feature_collection_reads_back_with_every_Turkish_letter()
    {
        using MemoryStream stream = new();

        long rows = await LayerExportRows.WriteEsriJsonAsync(
            stream, Layer, new TurkishRows(), Described, 100, CancellationToken.None);

        Assert.Equal(Values.Length, rows);

        JsonElement body = JsonDocument.Parse(stream.ToArray()).RootElement;
        string[] fields = [.. body.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()!)];

        foreach (string name in Names)
        {
            Assert.Contains(name, fields);
        }

        JsonElement[] features = [.. body.GetProperty("features").EnumerateArray()];
        Assert.Equal(Values.Length, features.Length);

        for (int r = 0; r < Values.Length; r++)
        {
            JsonElement attributes = features[r].GetProperty("attributes");

            for (int c = 0; c < Names.Length; c++)
            {
                JsonElement value = attributes.GetProperty(Names[c]);
                string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();

                Assert.Equal(Convert.ToString(Values[r][c], CultureInfo.InvariantCulture), text);
            }
        }
    }

    /// <summary>The fields and the rows GDAL reads from a file, every value as text.</summary>
    private static async Task<(List<string> Fields, List<Dictionary<string, string?>> Rows)> ReadBackAsync(
        string archive, string layer)
    {
        List<string> fields = [];
        List<Dictionary<string, string?>> rows = [];

        (JsonDocument? header, JsonDocument? trailer) = await Reader().StreamAsync(
            new { op = "features", archive, layer },
            TimeSpan.FromSeconds(60),
            line =>
            {
                Dictionary<string, string?> row = new(StringComparer.Ordinal);

                foreach (JsonProperty value in line.RootElement.GetProperty("v").EnumerateObject())
                {
                    row[value.Name] = value.Value.ValueKind switch
                    {
                        JsonValueKind.Null => null,
                        JsonValueKind.String => value.Value.GetString(),
                        _ => value.Value.GetRawText(),
                    };
                }

                rows.Add(row);
            });

        using (header)
        using (trailer)
        {
            Assert.True(header is not null, $"GDAL read no header from '{archive}'.");
            Assert.True(header!.RootElement.GetProperty("ok").GetBoolean(), header.RootElement.ToString());

            fields.AddRange(header.RootElement.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()!));
        }

        return (fields, rows);
    }

    /// <summary>The three rows, as a layer's source answers a page; nothing past the first.</summary>
    private sealed class TurkishRows : IFeatureSource
    {
        private static readonly FeatureSchema Schema = new(["objectid", .. Names]);

        public FeatureSchema SchemaFor(FeatureQuery query) => Schema;

        public Task<LayerDescription> DescribeAsync(CancellationToken cancellationToken) => Task.FromResult(Described);

        public Task<long> CountAsync(FeatureQuery query, CancellationToken cancellationToken) =>
            Task.FromResult((long)Values.Length);

        public Task<long> CountUpToAsync(FeatureQuery query, long ceiling, CancellationToken cancellationToken) =>
            Task.FromResult(Math.Min(ceiling, Values.Length));

        public async IAsyncEnumerable<Feature> ReadAsync(
            FeatureQuery query,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (query.Offset > 0)
            {
                yield break;
            }

            for (int r = 0; r < Values.Length; r++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                object?[] values = [r + 1, .. Values[r]];

                yield return new Feature(
                    (r + 1).ToString(CultureInfo.InvariantCulture), new Point(29 + r, 39 + r), Schema, values);
            }

            await Task.CompletedTask;
        }
    }
}
