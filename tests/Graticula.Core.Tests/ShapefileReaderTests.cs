using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Graticula.Features;
using Graticula.Formats;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// The shapefile reader, against files another implementation wrote.
/// </summary>
/// <remarks>
/// <para>
/// <b>The corpus is written by pyshp, not by this reader.</b> A parser verified
/// only against files it was written alongside proves that it agrees with
/// itself. pyshp is an independent implementation of the same published
/// specification, so a disagreement is evidence rather than a coincidence — and
/// <c>osm_real</c> holds fifty polygons taken straight out of the PostGIS corpus,
/// so the geometry is real rather than invented.
/// </para>
/// <para>
/// Regenerate with <c>tools/make-shapefile-corpus.py</c>.
/// </para>
/// </remarks>
public sealed class ShapefileReaderTests
{
    private static string Corpus =>
        Path.Combine(AppContext.BaseDirectory, "corpus", "shapefile");

    private static ImportedDataset Read(
        string name, Encoding? encoding = null, int srid = 4326)
    {
        string shp = Path.Combine(Corpus, name + ".shp");
        string dbf = Path.Combine(Corpus, name + ".dbf");

        Assert.True(
            File.Exists(shp),
            $"The corpus file {shp} is missing. Regenerate it with "
            + "tools/make-shapefile-corpus.py — these tests FAIL rather than skip, because a "
            + "parser suite that goes green with no files reports that a format is supported.");

        Assert.True(
            ShapefileReader.TryRead(
                File.ReadAllBytes(shp),
                File.Exists(dbf) ? File.ReadAllBytes(dbf) : [],
                srid,
                encoding ?? Encoding.UTF8,
                ImportLimits.Default,
                out ImportedDataset? dataset,
                out string? error),
            error);

        return dataset!;
    }

    private static string Refused(string name, Encoding? encoding = null)
    {
        Assert.False(
            ShapefileReader.TryRead(
                File.ReadAllBytes(Path.Combine(Corpus, name + ".shp")),
                File.ReadAllBytes(Path.Combine(Corpus, name + ".dbf")),
                4326,
                encoding ?? Encoding.UTF8,
                ImportLimits.Default,
                out _,
                out string? error));

        return error!;
    }

    // ---------- geometry ----------

    [Fact]
    public void Points_and_their_attributes_come_back()
    {
        ImportedDataset dataset = Read("points");

        Assert.Equal(GeometryKind.Point, dataset.GeometryType);
        Assert.Equal(2, dataset.Features.Count);

        Point first = Assert.IsType<Point>(dataset.Features[0].Geometry);

        Assert.Equal(10.0, first.X);
        Assert.Equal(20.0, first.Y);

        Assert.Equal("first", dataset.Features[0].Values["name"].GetString());
        Assert.Equal(1, dataset.Features[0].Values["id"].GetInt32());
        Assert.Equal(1.5, dataset.Features[0].Values["value"].GetDouble());
    }

    /// <summary>
    /// A ring inside another is a hole, not a second polygon.
    /// </summary>
    /// <remarks>
    /// <b>The part of the format most often got wrong.</b> A shapefile polygon
    /// record is a flat list of rings; the nesting is carried entirely by their
    /// winding. Reading each ring as its own polygon draws a lake as an island
    /// sitting on top of its own lake, which renders convincingly and gives the
    /// wrong area.
    /// </remarks>
    [Fact]
    public void A_ring_wound_the_other_way_is_a_hole()
    {
        ImportedDataset dataset = Read("holed");

        MultiPolygon multi = Assert.IsType<MultiPolygon>(
            Assert.Single(dataset.Features).Geometry);

        Polygon only = Assert.Single(multi.Parts);

        Assert.Single(only.Holes);
        Assert.Equal(5, only.Shell.Coordinates.Count);
    }

    [Fact]
    public void Two_outer_rings_in_one_record_are_two_polygons()
    {
        ImportedDataset dataset = Read("twoparts");

        MultiPolygon multi = Assert.IsType<MultiPolygon>(
            Assert.Single(dataset.Features).Geometry);

        Assert.Equal(2, multi.Parts.Count);
        Assert.All(multi.Parts, p => Assert.Empty(p.Holes));
    }

    [Fact]
    public void A_multi_part_line_keeps_its_parts()
    {
        ImportedDataset dataset = Read("lines");

        Assert.Equal(GeometryKind.MultiLineString, dataset.GeometryType);

        MultiLineString second = Assert.IsType<MultiLineString>(dataset.Features[1].Geometry);

        Assert.Equal(2, second.Parts.Count);
    }

    /// <summary>
    /// A null shape is a feature with no location, not a row to drop.
    /// </summary>
    /// <remarks>
    /// Dropping it would change the answer to a count, and the attributes it
    /// carries are somebody's data.
    /// </remarks>
    [Fact]
    public void A_null_shape_keeps_its_attributes()
    {
        ImportedDataset dataset = Read("withnull");

        Assert.Equal(2, dataset.Features.Count);
        Assert.NotNull(dataset.Features[0].Geometry);
        Assert.Null(dataset.Features[1].Geometry);
        Assert.Equal("nowhere", dataset.Features[1].Values["label"].GetString());
    }

    // ---------- real data ----------

    /// <summary>
    /// Fifty real polygons out of the PostGIS corpus survive the read.
    /// </summary>
    /// <remarks>
    /// <b>Real geometry is the point.</b> Hand-built rectangles exercise the
    /// happy path of a parser and none of what actual exports contain — long
    /// rings, repeated vertices, coordinates that need every digit of a double.
    /// </remarks>
    [Fact]
    public void Real_polygons_from_the_corpus_read_without_loss()
    {
        ImportedDataset dataset = Read("osm_real");

        Assert.Equal(50, dataset.Features.Count);

        Assert.All(dataset.Features, f =>
        {
            MultiPolygon multi = Assert.IsType<MultiPolygon>(f.Geometry);

            Assert.NotEmpty(multi.Parts);

            foreach (Polygon part in multi.Parts)
            {
                // A ring is closed or it is not a ring. An off-by-one in the
                // part index would produce open rings that still draw.
                Assert.True(
                    part.Shell.Coordinates.Count >= LinearRing.MinimumCoordinates,
                    "A shell came back with too few points to be a ring.");

                Assert.Equal(
                    (part.Shell.Coordinates.X(0), part.Shell.Coordinates.Y(0)),
                    (part.Shell.Coordinates.X(part.Shell.Coordinates.Count - 1),
                     part.Shell.Coordinates.Y(part.Shell.Coordinates.Count - 1)));
            }
        });

        // Every feature carries its name, and the names are Turkish.
        Assert.All(dataset.Features, f => Assert.True(f.Values.ContainsKey("name")));
    }

    [Fact]
    public void Coordinates_are_read_at_full_precision()
    {
        // A shapefile stores doubles. Reading them through a float, or through
        // a round-trip that formats and reparses, loses digits that matter at
        // Web Mercator's scale.
        ImportedDataset dataset = Read("osm_real");

        MultiPolygon first = Assert.IsType<MultiPolygon>(dataset.Features[0].Geometry);

        double x = first.Parts[0].Shell.Coordinates.X(0);

        Assert.NotEqual(x, Math.Round(x, 4));
    }

    // ---------- encoding ----------

    [Fact]
    public void Turkish_text_reads_correctly_when_the_encoding_is_right()
    {
        ImportedDataset utf8 = Read("turkish_utf8", Encoding.UTF8);

        Assert.Equal("Şişli Çayırı", utf8.Features[0].Values["ad"].GetString());

        ImportedDataset ansi = Read(
            "turkish_cp1254", CodePagesEncoding(1254));

        Assert.Equal("Şişli Çayırı", ansi.Features[0].Values["ad"].GetString());
    }

    /// <summary>
    /// The wrong encoding produces different text, which is the whole risk.
    /// </summary>
    /// <remarks>
    /// <b>This is why the import refuses to guess (owner decision, Q-98).</b>
    /// Reading Windows-1254 bytes as UTF-8 does not throw and does not look
    /// broken at a glance — it produces a string, and the damage surfaces months
    /// later in somebody's map labels.
    /// </remarks>
    [Fact]
    public void Reading_the_wrong_encoding_corrupts_silently_rather_than_failing()
    {
        ImportedDataset wrong = Read("turkish_cp1254", Encoding.UTF8);

        string read = wrong.Features[0].Values["ad"].GetString()!;

        Assert.NotEqual("Şişli Çayırı", read);

        // No exception, no empty string — just different text. That is the
        // failure mode the refusal exists to prevent.
        Assert.False(string.IsNullOrWhiteSpace(read));
    }

    private static Encoding CodePagesEncoding(int codePage)
    {
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage);
    }

    // ---------- refusals ----------

    [Fact]
    public void A_file_that_is_not_a_shapefile_is_refused_by_its_file_code()
    {
        Assert.False(
            ShapefileReader.TryRead(
                Encoding.UTF8.GetBytes(new string('x', 200)),
                [],
                4326,
                Encoding.UTF8,
                ImportLimits.Default,
                out _,
                out string? error));

        Assert.Contains("9994", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_shapefile_is_refused_rather_than_half_read()
    {
        byte[] whole = File.ReadAllBytes(Path.Combine(Corpus, "osm_real.shp"));

        Assert.False(
            ShapefileReader.TryRead(
                whole.AsSpan(0, whole.Length / 2).ToArray(),
                [],
                4326,
                Encoding.UTF8,
                ImportLimits.Default,
                out _,
                out string? error));

        Assert.Contains("truncated", error!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A .dbf with a different number of records than the .shp is refused.
    /// </summary>
    /// <remarks>
    /// <b>Attributes are matched to shapes by position and nothing else.</b> A
    /// user who zips a .shp from one export beside a .dbf from another gets a
    /// file where every attribute after the first missing record belongs to the
    /// wrong shape — invisible on a map, obvious in a table nobody opens.
    /// </remarks>
    [Fact]
    public void A_dbf_that_does_not_match_the_shp_is_refused()
    {
        Assert.False(
            ShapefileReader.TryRead(
                File.ReadAllBytes(Path.Combine(Corpus, "osm_real.shp")),
                File.ReadAllBytes(Path.Combine(Corpus, "points.dbf")),
                4326,
                Encoding.UTF8,
                ImportLimits.Default,
                out _,
                out string? error));

        Assert.Contains("must match", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_past_the_feature_limit_is_refused()
    {
        Assert.False(
            ShapefileReader.TryRead(
                File.ReadAllBytes(Path.Combine(Corpus, "osm_real.shp")),
                [],
                4326,
                Encoding.UTF8,
                new ImportLimits(10, 50_000_000, 250),
                out _,
                out string? error));

        Assert.Contains("features", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_past_the_vertex_limit_is_refused()
    {
        Assert.False(
            ShapefileReader.TryRead(
                File.ReadAllBytes(Path.Combine(Corpus, "osm_real.shp")),
                [],
                4326,
                Encoding.UTF8,
                new ImportLimits(1_000_000, 100, 250),
                out _,
                out string? error));

        Assert.Contains("vertices", error!, StringComparison.Ordinal);
    }

    // ---------- columns ----------

    [Fact]
    public void Column_types_come_from_the_values_rather_than_the_dbf_declaration()
    {
        // Both import paths infer the same way, so a numeric DBF column holding
        // only integers becomes an integer column rather than a double.
        ImportedDataset dataset = Read("points");

        InferredColumn id = dataset.Columns.Single(c => c.Name == "id");
        InferredColumn value = dataset.Columns.Single(c => c.Name == "value");
        InferredColumn name = dataset.Columns.Single(c => c.Name == "name");

        Assert.Equal(FieldType.Integer, id.Type);
        Assert.Equal(FieldType.Double, value.Type);
        Assert.Equal(FieldType.Text, name.Type);
    }

    /// <summary>
    /// A hole is a hole even when the file winds its rings the wrong way.
    /// </summary>
    /// <remarks>
    /// <b>Two of the fifty real polygons in this corpus do exactly that.</b> The
    /// specification says an outer ring is clockwise and a hole
    /// counter-clockwise; these carry the opposite, which is the OGC convention
    /// and what an ordinary export tool chain produced. Read by winding they
    /// became two overlapping shells, and PostGIS reported <em>nested shells</em>
    /// on import — 48 of 50 valid. Grouping by containment instead makes it 50.
    /// </remarks>
    [Fact]
    public void A_hole_is_recognised_by_containment_rather_than_winding()
    {
        ImportedDataset dataset = Read("osm_real");

        int withHoles = dataset.Features.Count(f =>
            f.Geometry is MultiPolygon m && m.Parts.Any(p => p.Holes.Count > 0));

        Assert.Equal(2, withHoles);

        // And nothing became two shells where one sits inside the other, which
        // is the shape PostGIS refuses.
        Assert.All(dataset.Features, f =>
        {
            MultiPolygon multi = Assert.IsType<MultiPolygon>(f.Geometry);

            for (int i = 0; i < multi.Parts.Count; i++)
            {
                for (int j = 0; j < multi.Parts.Count; j++)
                {
                    if (i == j)
                    {
                        continue;
                    }

                    Assert.False(
                        Inside(multi.Parts[j].Shell, multi.Parts[i].Shell),
                        "One shell sits inside another, which is a hole read as a polygon.");
                }
            }
        });
    }

    /// <summary>Whether the first shell's first vertex is inside the second.</summary>
    private static bool Inside(LinearRing inner, LinearRing outer)
    {
        double x = inner.Coordinates.X(0);
        double y = inner.Coordinates.Y(0);

        bool within = false;

        for (int i = 0, j = outer.Coordinates.Count - 1; i < outer.Coordinates.Count; j = i++)
        {
            double xi = outer.Coordinates.X(i);
            double yi = outer.Coordinates.Y(i);
            double xj = outer.Coordinates.X(j);
            double yj = outer.Coordinates.Y(j);

            if (yi > y != yj > y && x < ((xj - xi) * (y - yi) / (yj - yi)) + xi)
            {
                within = !within;
            }
        }

        return within;
    }
}
