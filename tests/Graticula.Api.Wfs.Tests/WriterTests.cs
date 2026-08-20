using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Graticula.Features;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Api.Wfs.Tests;

/// <summary>What comes out on the wire.</summary>
/// <remarks>
/// <b>Asserted against the parsed document rather than against a string.</b> A
/// golden-string test on XML fails on whitespace and namespace-prefix choices that
/// mean nothing, and passes when an element moves to the wrong parent — which
/// means everything.
/// </remarks>
public sealed class WriterTests
{
    private static readonly XNamespace Wfs = WfsNames.Wfs;
    private static readonly XNamespace Gml = WfsNames.Gml;
    private static readonly XNamespace Ows = WfsNames.Ows;
    private static readonly XNamespace Fes = WfsNames.Fes;
    private static readonly XNamespace Xsd = WfsNames.Xsd;
    private static readonly XNamespace Xsi = WfsNames.Xsi;

    private static readonly IReadOnlyList<FieldDescription> Fields =
    [
        new("objectid", FieldType.Integer, Nullable: false, MaxLength: null),
        new("name", FieldType.Text, Nullable: true, MaxLength: 100),
    ];

    private static WfsFeatureType Type(int srid = 3857, GeometryKind kind = GeometryKind.Polygon) =>
        new("tr_il", "Provinces", null, srid, kind, "geom", Fields, null);

    private static async Task<XElement> WriteAsync(
        Func<Stream, Task> write)
    {
        using MemoryStream stream = new();

        await write(stream);

        stream.Position = 0;

        return XElement.Load(stream);
    }

    private static Feature Feature(string id, Geometry? geometry, params object?[] values) =>
        new(id, geometry, new FeatureSchema([.. Fields.Select(f => f.Name)]), values);

    private static async IAsyncEnumerable<Feature> One(Feature feature)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield return feature;
    }

    // ---------- GML geometry ----------

    [Fact]
    public async Task A_polygon_is_written_as_gml_32_with_an_id()
    {
        Polygon polygon = new(new LinearRing(XySequence.Wrap([0, 0, 10, 0, 10, 10, 0, 0])));

        XElement root = await WriteAsync(stream => new GmlFeatureCollectionWriter(Type(), 3857, "https://example/wfs")
            .WriteAsync(stream, One(Feature("1", polygon, 1, "Ankara")), 1, 1,
                DateTimeOffset.UnixEpoch, CancellationToken.None));

        XElement shape = root.Descendants(Gml + "Polygon").Single();

        // gml:id is mandatory on every GML 3.2 object, and it is derived from the
        // feature so it is stable between requests.
        Assert.Equal("tr_il.1.geom", (string?)shape.Attribute(Gml + "id"));
        Assert.Equal("urn:ogc:def:crs:EPSG::3857", (string?)shape.Attribute("srsName"));
        Assert.Equal("0 0 10 0 10 10 0 0", shape.Descendants(Gml + "posList").Single().Value);
    }

    [Fact]
    public async Task A_multipolygon_is_a_multisurface_because_gml_32_renamed_it()
    {
        // gml:MultiPolygon is GML 3.1. A 3.2 schema rejects it, and the client
        // reports a broken layer rather than a bad document.
        MultiPolygon multi = new(
        [
            new Polygon(new LinearRing(XySequence.Wrap([0, 0, 1, 0, 1, 1, 0, 0]))),
            new Polygon(new LinearRing(XySequence.Wrap([5, 5, 6, 5, 6, 6, 5, 5]))),
        ]);

        XElement root = await WriteAsync(stream =>
            new GmlFeatureCollectionWriter(Type(kind: GeometryKind.MultiPolygon), 3857, "https://example/wfs")
                .WriteAsync(stream, One(Feature("1", multi, 1, "x")), 1, 1,
                    DateTimeOffset.UnixEpoch, CancellationToken.None));

        Assert.Single(root.Descendants(Gml + "MultiSurface"));
        Assert.Empty(root.Descendants(Gml + "MultiPolygon"));
        Assert.Equal(2, root.Descendants(Gml + "surfaceMember").Count());
    }

    [Fact]
    public async Task A_multilinestring_is_a_multicurve()
    {
        MultiLineString multi = new(
        [
            new LineString(XySequence.Wrap([0, 0, 1, 1])),
        ]);

        XElement root = await WriteAsync(stream =>
            new GmlFeatureCollectionWriter(Type(kind: GeometryKind.MultiLineString), 3857, "https://example/wfs")
                .WriteAsync(stream, One(Feature("1", multi, 1, "x")), 1, 1,
                    DateTimeOffset.UnixEpoch, CancellationToken.None));

        Assert.Single(root.Descendants(Gml + "MultiCurve"));
        Assert.Single(root.Descendants(Gml + "curveMember"));
    }

    [Fact]
    public async Task In_4326_latitude_is_written_first()
    {
        // <b>The trap WFS is best known for.</b> Under urn:ogc:def:crs:EPSG::4326
        // the axis order is EPSG's — latitude then longitude — and writing
        // longitude first puts every feature somewhere else with no error
        // anywhere.
        Point point = new(32.85, 39.93);

        XElement root = await WriteAsync(stream =>
            new GmlFeatureCollectionWriter(Type(4326, GeometryKind.Point), 4326, "https://example/wfs")
                .WriteAsync(stream, One(Feature("1", point, 1, "Ankara")), 1, 1,
                    DateTimeOffset.UnixEpoch, CancellationToken.None));

        Assert.Equal("39.93 32.85", root.Descendants(Gml + "pos").Single().Value);
    }

    [Fact]
    public async Task In_a_projected_reference_easting_is_written_first()
    {
        Point point = new(3657000, 4855000);

        XElement root = await WriteAsync(stream =>
            new GmlFeatureCollectionWriter(Type(3857, GeometryKind.Point), 3857, "https://example/wfs")
                .WriteAsync(stream, One(Feature("1", point, 1, "Ankara")), 1, 1,
                    DateTimeOffset.UnixEpoch, CancellationToken.None));

        Assert.Equal("3657000 4855000", root.Descendants(Gml + "pos").Single().Value);
    }

    // ---------- GML feature collection ----------

    [Fact]
    public async Task The_collection_carries_the_counts_wfs_requires()
    {
        XElement root = await WriteAsync(stream => new GmlFeatureCollectionWriter(Type(), 3857, "https://example/wfs")
            .WriteAsync(
                stream,
                One(Feature("1", new Point(1, 2), 1, "x")),
                numberMatched: 4321,
                numberReturned: 1,
                DateTimeOffset.UnixEpoch,
                CancellationToken.None));

        Assert.Equal(Wfs + "FeatureCollection", root.Name);
        Assert.Equal("4321", (string?)root.Attribute("numberMatched"));
        Assert.Equal("1", (string?)root.Attribute("numberReturned"));
        Assert.Equal("1970-01-01T00:00:00Z", (string?)root.Attribute("timeStamp"));
    }

    [Fact]
    public async Task An_unknown_match_count_says_unknown_rather_than_zero()
    {
        XElement root = await WriteAsync(stream => new GmlFeatureCollectionWriter(Type(), 3857, "https://example/wfs")
            .WriteAsync(stream, One(Feature("1", new Point(1, 2), 1, "x")), null, 1,
                DateTimeOffset.UnixEpoch, CancellationToken.None));

        Assert.Equal("unknown", (string?)root.Attribute("numberMatched"));
    }

    [Fact]
    public async Task A_null_value_is_nil_rather_than_absent()
    {
        // Absent means *not asked for* and nil means *asked for and empty*. A
        // client reading a schema where every property is optional cannot tell
        // them apart if they are written the same way.
        XElement root = await WriteAsync(stream => new GmlFeatureCollectionWriter(Type(), 3857, "https://example/wfs")
            .WriteAsync(stream, One(Feature("1", null, 1, null)), 1, 1,
                DateTimeOffset.UnixEpoch, CancellationToken.None));

        XNamespace ns = WfsNames.Namespace;

        XElement name = root.Descendants(ns + "name").Single();
        XElement geometry = root.Descendants(ns + "geom").Single();

        Assert.Equal("true", (string?)name.Attribute(Xsi + "nil"));
        Assert.Equal("true", (string?)geometry.Attribute(Xsi + "nil"));
        Assert.Empty(geometry.Elements());
    }

    [Fact]
    public async Task A_feature_is_identified_the_way_the_stored_query_takes_it_back()
    {
        XElement root = await WriteAsync(stream => new GmlFeatureCollectionWriter(Type(), 3857, "https://example/wfs")
            .WriteAsync(stream, One(Feature("42", new Point(1, 2), 42, "x")), 1, 1,
                DateTimeOffset.UnixEpoch, CancellationToken.None));

        XNamespace ns = WfsNames.Namespace;

        string gmlId = (string)root.Descendants(ns + "tr_il").Single().Attribute(Gml + "id")!;

        Assert.Equal("tr_il.42", gmlId);

        Assert.True(WfsFeatureType.TrySplitResourceId(gmlId, out string type, out string id));
        Assert.Equal("tr_il", type);
        Assert.Equal("42", id);
    }

    // ---------- GeoJSON ----------

    [Fact]
    public async Task GeoJson_is_written_longitude_first_whatever_the_gml_rule_says()
    {
        // GeoJSON fixes the order in the specification, so none of the axis
        // reasoning applies. The asymmetry with GML is the specifications', not
        // this server's.
        using MemoryStream stream = new();

        await new GeoJsonFeatureCollectionWriter(Type(4326, GeometryKind.Point))
            .WriteAsync(stream, One(Feature("1", new Point(32.85, 39.93), 1, "Ankara")),
                1, 1, DateTimeOffset.UnixEpoch, CancellationToken.None);

        string json = Encoding.UTF8.GetString(stream.ToArray());

        Assert.Contains("\"coordinates\":[32.85,39.93]", json, StringComparison.Ordinal);
        Assert.Contains("\"numberMatched\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"tr_il.1\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"crs\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeoJson_writes_a_polygons_holes_after_its_shell()
    {
        Polygon polygon = new(
            new LinearRing(XySequence.Wrap([0, 0, 10, 0, 10, 10, 0, 0])),
            [new LinearRing(XySequence.Wrap([2, 2, 4, 2, 4, 4, 2, 2]))]);

        using MemoryStream stream = new();

        await new GeoJsonFeatureCollectionWriter(Type())
            .WriteAsync(stream, One(Feature("1", polygon, 1, "x")), 1, 1,
                DateTimeOffset.UnixEpoch, CancellationToken.None);

        string json = Encoding.UTF8.GetString(stream.ToArray());

        Assert.Contains(
            "\"coordinates\":[[[0,0],[10,0],[10,10],[0,0]],[[2,2],[4,2],[4,4],[2,2]]]",
            json,
            StringComparison.Ordinal);
    }

    // ---------- capabilities ----------

    [Fact]
    public async Task The_capabilities_declare_the_two_things_that_are_false()
    {
        XElement root = await WriteAsync(stream => CapabilitiesDocument.WriteAsync(
            stream, "https://example/wfs", "Graticula", [Type()], CancellationToken.None));

        Assert.Equal(Wfs + "WFS_Capabilities", root.Name);
        Assert.Equal("2.0.0", (string?)root.Attribute("version"));

        // A client reads these to decide what to offer its operator. Declaring
        // transactions TRUE would put an edit button in front of somebody.
        Assert.Equal("FALSE", Constraint(root, "ImplementsTransactionalWFS"));
        Assert.Equal("FALSE", Constraint(root, "ImplementsLockingWFS"));
        Assert.Equal("TRUE", Constraint(root, "ImplementsBasicWFS"));
        Assert.Equal("TRUE", Constraint(root, "ImplementsResultPaging"));

        static string? Constraint(XElement root, string name) => root
            .Descendants()
            .Where(e => e.Name.LocalName == "Constraint"
                && (string?)e.Attribute("name") == name)
            .Select(e => e.Element(Ows + "DefaultValue")?.Value)
            .FirstOrDefault();
    }

    [Fact]
    public async Task Each_feature_type_is_listed_with_its_qualified_name_and_reference()
    {
        XElement root = await WriteAsync(stream => CapabilitiesDocument.WriteAsync(
            stream, "https://example/wfs", "Graticula", [Type()], CancellationToken.None));

        XElement type = root.Descendants(Wfs + "FeatureType").Single();

        Assert.Equal("graticula:tr_il", type.Element(Wfs + "Name")!.Value);
        Assert.Equal("urn:ogc:def:crs:EPSG::3857", type.Element(Wfs + "DefaultCRS")!.Value);

        // The prefix is bound at the root, or the qualified name above cannot be
        // resolved by whoever copies it.
        Assert.Equal(
            WfsNames.Namespace, root.GetNamespaceOfPrefix(WfsNames.Prefix)?.NamespaceName);
    }

    [Fact]
    public async Task A_bounding_box_is_written_only_where_it_can_be_written_truthfully()
    {
        // ows:WGS84BoundingBox is WGS 84 by definition. A layer in another
        // reference would need a projection to produce one, so the element is
        // omitted rather than filled with the layer's own numbers under a WGS 84
        // label. Q-125.
        WfsFeatureType projected = Type() with { Extent = new Envelope(0, 0, 10, 10) };
        WfsFeatureType geographic = Type(4326) with { Extent = new Envelope(25, 35, 45, 43) };

        XElement without = await WriteAsync(stream => CapabilitiesDocument.WriteAsync(
            stream, "https://example/wfs", "Graticula", [projected], CancellationToken.None));

        XElement with = await WriteAsync(stream => CapabilitiesDocument.WriteAsync(
            stream, "https://example/wfs", "Graticula", [geographic], CancellationToken.None));

        Assert.Empty(without.Descendants(Ows + "WGS84BoundingBox"));

        XElement box = with.Descendants(Ows + "WGS84BoundingBox").Single();

        // Longitude first here, unlike the GML above: this element is defined that
        // way whatever the CRS's own axis order says.
        Assert.Equal("25 35", box.Element(Ows + "LowerCorner")!.Value);
        Assert.Equal("45 43", box.Element(Ows + "UpperCorner")!.Value);
    }

    [Fact]
    public async Task A_caller_who_may_see_nothing_still_gets_a_valid_document()
    {
        // <b>An empty wfs:FeatureTypeList is not valid</b> — the schema requires at
        // least one member — and the list a caller may see is legitimately empty on
        // a server whose layers are all private. So the element is omitted rather
        // than written empty, and the anonymous caller gets a document that
        // validates and lists nothing.
        XElement root = await WriteAsync(stream => CapabilitiesDocument.WriteAsync(
            stream, "https://example/wfs", "Graticula", [], CancellationToken.None));

        Assert.Empty(root.Descendants(Wfs + "FeatureTypeList"));
        Assert.Single(root.Descendants(Ows + "ServiceIdentification"));
        Assert.Single(root.Descendants(Fes + "Filter_Capabilities"));
    }

    [Fact]
    public async Task The_service_provider_carries_the_contact_the_schema_requires()
    {
        // ows:ServiceProvider makes ServiceContact mandatory and every child of it
        // optional. Found by validating against the published schema, not by
        // reading it.
        XElement root = await WriteAsync(stream => CapabilitiesDocument.WriteAsync(
            stream, "https://example/wfs", "Graticula", [Type()], CancellationToken.None));

        Assert.Single(root.Descendants(Ows + "ServiceContact"));
    }

    [Fact]
    public async Task The_collection_says_where_its_own_schema_is()
    {
        // Without this a validator — and a strict client — has the feature elements
        // and nowhere to learn what they are.
        XElement root = await WriteAsync(stream =>
            new GmlFeatureCollectionWriter(Type(), 3857, "https://example/wfs")
                .WriteAsync(stream, One(Feature("1", new Point(1, 2), 1, "x")), 1, 1,
                    DateTimeOffset.UnixEpoch, CancellationToken.None));

        string hint = (string)root.Attribute(Xsi + "schemaLocation")!;

        Assert.Contains(WfsNames.Wfs, hint, StringComparison.Ordinal);
        Assert.Contains(WfsNames.Namespace, hint, StringComparison.Ordinal);
        Assert.Contains("request=DescribeFeatureType", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gml_id_this_server_wrote_splits_back_into_a_type_and_an_identity()
    {
        // <b>The round trip a ResourceId depends on.</b> A client sends back the
        // exact string this server wrote, and it was being refused as not an
        // identifier for a while — because only the stored query split it.
        Assert.True(WfsFeatureType.TrySplitResourceId("tr_il.2367", out string type, out string id));
        Assert.Equal("tr_il", type);
        Assert.Equal("2367", id);

        // A dotted identity keeps its dots: the split is on the last one.
        Assert.True(WfsFeatureType.TrySplitResourceId("a.b.1", out type, out id));
        Assert.Equal("a.b", type);
        Assert.Equal("1", id);

        // A bare identity is not one, and must not be mangled into one.
        Assert.False(WfsFeatureType.TrySplitResourceId("2367", out _, out _));
        Assert.False(WfsFeatureType.TrySplitResourceId("tr_il.", out _, out _));
        Assert.False(WfsFeatureType.TrySplitResourceId(".2367", out _, out _));
    }

    [Fact]
    public async Task The_filter_capabilities_list_what_the_reader_accepts()
    {
        XElement root = await WriteAsync(stream => CapabilitiesDocument.WriteAsync(
            stream, "https://example/wfs", "Graticula", [Type()], CancellationToken.None));

        string[] advertised =
        [
            .. root.Descendants(Fes + "ComparisonOperator")
                .Select(e => (string)e.Attribute("name")!),
        ];

        Assert.Equal(CapabilitiesDocument.ComparisonOperators, advertised);

        string[] spatial =
        [
            .. root.Descendants(Fes + "SpatialOperator").Select(e => (string)e.Attribute("name")!),
        ];

        Assert.Equal(CapabilitiesDocument.SpatialOperators, spatial);
    }

    // ---------- schema ----------

    [Fact]
    public async Task The_schema_extends_the_gml_feature_type_and_names_every_property()
    {
        XElement root = await WriteAsync(stream => FeatureTypeSchema.WriteAsync(stream, [Type()], CancellationToken.None));

        Assert.Equal(Xsd + "schema", root.Name);
        Assert.Equal(WfsNames.Namespace, (string?)root.Attribute("targetNamespace"));

        XElement element = root.Elements(Xsd + "element").Single();

        Assert.Equal("tr_il", (string?)element.Attribute("name"));
        Assert.Equal("gml:AbstractFeature", (string?)element.Attribute("substitutionGroup"));

        Dictionary<string, string> properties = root
            .Descendants(Xsd + "element")
            .Where(e => e.Attribute("minOccurs") is not null)
            .ToDictionary(
                e => (string)e.Attribute("name")!,
                e => (string)e.Attribute("type")!,
                StringComparer.Ordinal);

        Assert.Equal("xsd:int", properties["objectid"]);
        Assert.Equal("xsd:string", properties["name"]);

        // Surface, not Polygon: GML 3.2 has no gml:PolygonPropertyType, and a
        // schema naming one fails to resolve.
        Assert.Equal("gml:SurfacePropertyType", properties["geom"]);
    }

    [Fact]
    public async Task Types_from_two_folders_share_one_schema()
    {
        // This asserted the opposite until the OGC conformance suite was pointed
        // at the server. Folders were namespaces, so a describe covering two of
        // them answered with a schema of xsd:import elements — which the
        // specification calls for, and which the suite cannot consume: it builds
        // its model from a describe naming no types, and a document of imports
        // declares no feature types. 264 of its tests could not run. Folders are
        // titles now, and the case is gone rather than handled.
        WfsFeatureType other = new(
            "tr_yol", "Roads", null, 3857, GeometryKind.LineString, "geom", Fields, null);

        XElement root = await WriteAsync(stream =>
            FeatureTypeSchema.WriteAsync(stream, [Type(), other], CancellationToken.None));

        Assert.Equal(WfsNames.Namespace, (string?)root.Attribute("targetNamespace"));

        // One import, and it is GML's. Ours are declarations, not references.
        XElement import = Assert.Single(root.Elements(Xsd + "import"));
        Assert.Equal(WfsNames.Gml, (string?)import.Attribute("namespace"));

        string[] declared =
        [
            .. root.Elements(Xsd + "element").Select(e => (string)e.Attribute("name")!),
        ];

        Assert.Equal(["tr_il", "tr_yol"], declared);
    }

    // ---------- stored queries ----------

    [Fact]
    public async Task The_required_stored_query_is_listed_and_described()
    {
        XElement list = await WriteAsync(stream =>
            StoredQueries.WriteListAsync(stream, [Type()], CancellationToken.None));

        Assert.Equal(
            WfsRequest.GetFeatureByIdQuery,
            (string?)list.Element(Wfs + "StoredQuery")!.Attribute("id"));

        Assert.Equal(
            "graticula:tr_il",
            list.Descendants(Wfs + "ReturnFeatureType").Single().Value);

        XElement described = await WriteAsync(stream =>
            StoredQueries.WriteDescriptionAsync(stream, [Type()], CancellationToken.None));

        Assert.Equal(
            "id",
            (string?)described.Descendants(Wfs + "Parameter").Single().Attribute("name"));
    }

    // ---------- faults ----------

    [Fact]
    public async Task A_refusal_is_an_exception_report_a_client_can_read()
    {
        XElement root = await WriteAsync(stream =>
            WfsFault.Invalid("typeNames", "no such type").WriteAsync(stream, CancellationToken.None));

        Assert.Equal(Ows + "ExceptionReport", root.Name);

        XElement exception = root.Element(Ows + "Exception")!;

        Assert.Equal("InvalidParameterValue", (string?)exception.Attribute("exceptionCode"));
        Assert.Equal("typeNames", (string?)exception.Attribute("locator"));
        Assert.Equal("no such type", exception.Element(Ows + "ExceptionText")!.Value);
    }

    // ---------- values ----------

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    [InlineData(1.5, "1.5")]
    [InlineData(42L, "42")]
    public void A_value_is_written_invariantly(object value, string expected)
    {
        Assert.Equal(expected, GmlFeatureCollectionWriter.Text(value));
    }

    [Fact]
    public void A_date_is_written_in_the_one_format_xml_schema_defines()
    {
        string text = GmlFeatureCollectionWriter.Text(
            new DateTime(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc));

        Assert.StartsWith("2026-08-19T12:00:00", text, StringComparison.Ordinal);
        Assert.EndsWith("Z", text, StringComparison.Ordinal);
        Assert.True(DateTime.TryParse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _));
    }
}
