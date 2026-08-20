using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The WMS surface, driven over HTTP the way a client drives it.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-041](../../docs/adr/ADR-041-the-map-renderer.md) condition 2.</b> Three
/// defects were found on the day this surface was built and every one of them
/// needed a running server: a capabilities document declaring the wrong encoding, a
/// GetFeatureInfo returning features with no attributes, and — the one that
/// matters — <b>every 1.3.0 refusal answering 500</b> because the exception writer
/// put its namespace in the wrong place. None of the three is visible in a unit
/// test of the writer, because the writer was correct about everything the unit
/// test asked it.
/// </para>
/// <para>
/// <b>So this asks for a refusal of every kind</b>, and reads the exception code
/// rather than the status. A test asserting only *it did not work* passes for a
/// request refused for a completely different reason.
/// </para>
/// </remarks>
public sealed class WmsConformanceTests : ArcGisClient
{
    private const string Wms = "http://www.opengis.net/wms";
    private const string Ogc = "http://www.opengis.net/ogc";

    /// <summary>A GetMap of somewhere, in whichever version.</summary>
    private static string MapUrl(
        string layer,
        string version = "1.3.0",
        string crs = "EPSG:4326",
        string bbox = "35,25,43,45",
        int width = 200,
        int height = 150,
        string format = "image/png",
        string extra = "")
    {
        string crsName = version == "1.3.0" ? "crs" : "srs";

        return $"/wms?service=WMS&version={version}&request=GetMap"
            + $"&layers={Uri.EscapeDataString(layer)}&styles="
            + $"&{crsName}={crs}&bbox={bbox}&width={width}&height={height}"
            + $"&format={Uri.EscapeDataString(format)}{extra}";
    }

    private async Task<(string MediaType, byte[] Body)> RawAsync(string path)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + path));
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (
            response.Content.Headers.ContentType?.MediaType ?? string.Empty,
            await response.Content.ReadAsByteArrayAsync());
    }

    private async Task<XDocument> XmlAsync(string path)
    {
        (_, byte[] body) = await RawAsync(path);

        return XDocument.Parse(System.Text.Encoding.UTF8.GetString(body));
    }

    /// <summary>The exception code a request is refused with, or null if it was not.</summary>
    /// <remarks>
    /// <b>Reads both versions' documents.</b> 1.3.0's report is namespaced and
    /// 1.1.1's is not, and a helper that looked only in one would report *not
    /// refused* for a perfectly good refusal in the other.
    /// </remarks>
    private async Task<string?> RefusalOfAsync(string path)
    {
        XDocument document = await XmlAsync(path);

        Assert.True(
            document.Root!.Name.LocalName == "ServiceExceptionReport",
            $"{path} was answered with <{document.Root.Name.LocalName}> rather than a refusal.");

        XElement? exception = document.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "ServiceException");

        Assert.NotNull(exception);

        return exception!.Attribute("code")?.Value;
    }

    /// <summary>Every named layer the capabilities document publishes.</summary>
    private async Task<IReadOnlyList<XElement>> PublishedAsync(string version = "1.3.0")
    {
        XDocument capabilities =
            await XmlAsync($"/wms?service=WMS&version={version}&request=GetCapabilities");

        return
        [
            .. capabilities
                .Descendants()
                .Where(e => e.Name.LocalName == "Layer"
                    && e.Elements().Any(c => c.Name.LocalName == "Name")),
        ];
    }

    private static string? Child(XElement layer, string name) =>
        layer.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    // ---------- the documents ----------

    [Fact]
    public async Task Capabilities_is_utf8_and_says_so()
    {
        // <b>Found on the first document this surface ever served.</b> XmlWriter over
        // a StringBuilder declares utf-16 whatever its settings say, because a .NET
        // string is utf-16 — honest about the buffer and wrong about the wire.
        // Parsers that trust the declaration fail on the first non-ASCII layer name.
        (string mediaType, byte[] body) = await RawAsync("/wms?service=WMS&request=GetCapabilities");

        Assert.Equal("text/xml", mediaType);

        string head = System.Text.Encoding.UTF8.GetString(body[..Math.Min(80, body.Length)]);

        Assert.Contains("utf-8", head, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("utf-16", head, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Both_versions_answer_their_own_document()
    {
        XDocument one30 = await XmlAsync("/wms?service=WMS&version=1.3.0&request=GetCapabilities");
        XDocument one11 = await XmlAsync("/wms?service=WMS&version=1.1.1&request=GetCapabilities");

        Assert.Equal(XName.Get("WMS_Capabilities", Wms), one30.Root!.Name);
        Assert.Equal("1.3.0", one30.Root.Attribute("version")?.Value);

        // 1.1.1 is not namespaced, and a document that carried the 1.3.0 namespace
        // would be refused by the DTD-validating clients this version exists for.
        Assert.Equal("WMT_MS_Capabilities", one11.Root!.Name.LocalName);
        Assert.Equal(string.Empty, one11.Root.Name.NamespaceName);
        Assert.Equal("1.1.1", one11.Root.Attribute("version")?.Value);
    }

    [Fact]
    public async Task Every_published_layer_carries_what_its_version_requires()
    {
        foreach (XElement layer in await PublishedAsync())
        {
            string name = Child(layer, "Name")!;

            // 1.3.0 makes EX_GeographicBoundingBox mandatory on a named layer. It
            // was absent from every layer of the first document this surface wrote,
            // which no client would have accepted.
            Assert.True(
                layer.Elements().Any(e => e.Name.LocalName == "EX_GeographicBoundingBox"),
                $"{name} publishes no EX_GeographicBoundingBox, which 1.3.0 requires.");

            Assert.True(
                layer.Elements().Any(e => e.Name.LocalName == "CRS"),
                $"{name} publishes no CRS.");
        }

        foreach (XElement layer in await PublishedAsync("1.1.1"))
        {
            string name = Child(layer, "Name")!;

            Assert.True(
                layer.Elements().Any(e => e.Name.LocalName == "LatLonBoundingBox"),
                $"{name} publishes no LatLonBoundingBox, which 1.1.1 requires.");

            Assert.True(
                layer.Elements().Any(e => e.Name.LocalName == "SRS"),
                $"{name} publishes SRS under another name; 1.1.1 has no CRS element.");
        }
    }

    // ---------- the map ----------

    [Fact]
    public async Task Every_published_layer_draws()
    {
        int drawn = 0;

        foreach (XElement layer in await PublishedAsync())
        {
            string name = Child(layer, "Name")!;

            XElement? box = layer.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "EX_GeographicBoundingBox");

            if (box is null)
            {
                continue;
            }

            // CRS:84 is longitude first whatever the version, which is what the
            // geographic bounding box is written in. Asking in it here keeps this
            // test about drawing rather than about axis order, which has its own.
            string west = Child(box, "westBoundLongitude")!;
            string east = Child(box, "eastBoundLongitude")!;
            string south = Child(box, "southBoundLatitude")!;
            string north = Child(box, "northBoundLatitude")!;

            (string mediaType, byte[] image) = await RawAsync(
                MapUrl(name, crs: "CRS:84", bbox: $"{west},{south},{east},{north}"));

            Assert.Equal("image/png", mediaType);
            Assert.Equal<byte>([0x89, 0x50, 0x4E, 0x47], image[..4]);

            drawn++;
        }

        Assert.True(drawn > 0, "No layer was drawn, so this test asserted nothing.");
    }

    [Fact]
    public async Task The_two_versions_transpose_the_same_extent_and_draw_the_same_map()
    {
        // <b>The most expensive trap in OGC protocols, asserted.</b> WMS 1.3.0 with
        // EPSG:4326 is latitude first and 1.1.1 never is. Read the wrong way round,
        // a request for Turkey draws the Indian Ocean, every layer comes back empty,
        // and it is diagnosed as a data problem.
        //
        // The extent is deliberately not square: a square one passes with the axes
        // swapped, which is exactly the test that would have let this through.
        string? layer = await AnyPublishedAsync();
        Assert.NotNull(layer);

        (_, byte[] one30) = await RawAsync(MapUrl(layer!, bbox: "36,26,41,44"));
        (_, byte[] one11) = await RawAsync(
            MapUrl(layer!, version: "1.1.1", bbox: "26,36,44,41"));

        Assert.Equal(one30, one11);
    }

    [Fact]
    public async Task A_transparent_png_is_transparent_and_a_jpeg_is_a_jpeg()
    {
        string? layer = await AnyPublishedAsync();
        Assert.NotNull(layer);

        // Somewhere with no data at all: the map matched nothing, which is a 200 and
        // a transparent image rather than a refusal. ADR-041 condition 5.
        (string mediaType, byte[] empty) = await RawAsync(
            MapUrl(layer!, bbox: "1,1,2,2", extra: "&transparent=true"));

        Assert.Equal("image/png", mediaType);
        Assert.Equal<byte>([0x89, 0x50, 0x4E, 0x47], empty[..4]);

        (string jpegType, byte[] jpeg) = await RawAsync(MapUrl(layer!, format: "image/jpeg"));

        Assert.Equal("image/jpeg", jpegType);
        Assert.Equal<byte>([0xFF, 0xD8, 0xFF], jpeg[..3]);
    }

    // ---------- feature info and legends ----------

    [Fact]
    public async Task Feature_info_returns_attributes_rather_than_bare_identities()
    {
        // <b>FeatureQuery.Fields empty means identity and geometry only</b>, and an
        // empty list is what you get by not thinking about it. The first
        // GetFeatureInfo this surface answered carried a feature id and no columns,
        // which reads as data with no attributes rather than as a query that asked
        // for none.
        string? layer = await FindQueryableWithFeaturesAsync();

        if (layer is null)
        {
            Assert.Fail("No published layer has features to identify, so this asserted nothing.");
            return;
        }

        (string mediaType, byte[] body) = await RawAsync(layer);

        Assert.Equal("application/json", mediaType);

        System.Text.Json.JsonElement document =
            System.Text.Json.JsonDocument.Parse(body).RootElement;

        System.Text.Json.JsonElement features = document.GetProperty("features");

        Assert.True(features.GetArrayLength() > 0, "Nothing was identified.");

        System.Text.Json.JsonElement properties = features[0].GetProperty("properties");

        // __layer is written by this server; anything else is the layer's own.
        Assert.True(
            properties.EnumerateObject().Count() > 1,
            "The identified feature carries no attributes of its own.");
    }

    [Fact]
    public async Task A_legend_is_an_image_of_the_asked_size()
    {
        string? layer = await AnyPublishedAsync();
        Assert.NotNull(layer);

        (string mediaType, byte[] image) = await RawAsync(
            $"/wms?service=WMS&version=1.3.0&request=GetLegendGraphic"
            + $"&layer={Uri.EscapeDataString(layer!)}&format=image/png&width=24&height=24");

        Assert.Equal("image/png", mediaType);
        Assert.Equal<byte>([0x89, 0x50, 0x4E, 0x47], image[..4]);
    }

    // ---------- refusals ----------

    [Theory]
    [InlineData("layers=no_such_layer", "LayerNotDefined")]
    [InlineData("format=image/gif", "InvalidFormat")]
    [InlineData("styles=something", "StyleNotDefined")]
    [InlineData("width=99999", "InvalidParameterValue")]
    [InlineData("crs=NOTACRS", "InvalidCRS")]
    [InlineData("bbox=1,2,3", "InvalidParameterValue")]
    [InlineData("time=notatime", "InvalidDimensionValue")]
    public async Task A_bad_parameter_is_refused_with_the_code_that_names_it(
        string replacement, string expected)
    {
        // <b>Every refusal of this surface answered 500 on the day it was built.</b>
        // The exception writer opened its root element in no namespace and then tried
        // to declare one as an attribute, which XmlWriter refuses. Nothing had
        // exercised a refusal path, so the whole surface looked healthy.
        string? layer = await AnyPublishedAsync();
        Assert.NotNull(layer);

        string url = MapUrl(layer!);
        string key = replacement[..replacement.IndexOf('=', StringComparison.Ordinal)];

        // Replace the parameter rather than appending: a duplicate would leave the
        // original in place and test nothing.
        string[] parts = url.Split('&');

        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                parts[i] = replacement;
            }
        }

        string amended = string.Join('&', parts);

        if (!amended.Contains(replacement, StringComparison.Ordinal))
        {
            amended += "&" + replacement;
        }

        Assert.Equal(expected, await RefusalOfAsync(amended));
    }

    [Fact]
    public async Task An_unknown_version_is_refused_on_a_map_and_answered_on_capabilities()
    {
        // GetCapabilities negotiates: a client asking what this server speaks must be
        // answered even when it guessed wrong, because that is the question. GetMap
        // does not: the version decides the axis order, and drawing 1.3.0 axes for a
        // 1.1.1 request is a map of somewhere else with no error anywhere.
        XDocument capabilities =
            await XmlAsync("/wms?service=WMS&version=9.9.9&request=GetCapabilities");

        Assert.Equal("WMS_Capabilities", capabilities.Root!.Name.LocalName);

        string? layer = await AnyPublishedAsync();
        Assert.NotNull(layer);

        Assert.Equal(
            "VersionNegotiationFailed",
            await RefusalOfAsync(MapUrl(layer!, version: "9.9.9")));
    }

    [Fact]
    public async Task A_refusal_is_namespaced_in_1_3_0_and_not_in_1_1_1()
    {
        string? layer = await AnyPublishedAsync();
        Assert.NotNull(layer);

        XDocument one30 = await XmlAsync(MapUrl(layer!, format: "image/gif"));
        XDocument one11 = await XmlAsync(MapUrl(layer!, version: "1.1.1", format: "image/gif"));

        Assert.Equal(XName.Get("ServiceExceptionReport", Ogc), one30.Root!.Name);
        Assert.Equal(string.Empty, one11.Root!.Name.NamespaceName);
    }

    [Fact]
    public async Task Query_layers_must_be_among_the_layers_drawn()
    {
        string? layer = await AnyPublishedAsync();
        Assert.NotNull(layer);

        Assert.Equal(
            "LayerNotDefined",
            await RefusalOfAsync(
                MapUrl(layer!).Replace("request=GetMap", "request=GetFeatureInfo", StringComparison.Ordinal)
                + "&query_layers=something_else&i=1&j=1"));
    }

    // ---------- helpers ----------

    private async Task<string?> AnyPublishedAsync()
    {
        foreach (XElement layer in await PublishedAsync())
        {
            if (Child(layer, "Name") is { Length: > 0 } name)
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>
    /// A GetFeatureInfo URL aimed at a layer that actually has something there.
    /// </summary>
    /// <remarks>
    /// <b>Aimed at the middle of the layer's own extent</b>, which is where a layer
    /// with any data at all has some. Picking a fixed coordinate would make this test
    /// about the test dataset rather than about the operation.
    /// </remarks>
    private async Task<string?> FindQueryableWithFeaturesAsync()
    {
        foreach (XElement layer in await PublishedAsync())
        {
            string name = Child(layer, "Name")!;

            XElement? box = layer.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "EX_GeographicBoundingBox");

            if (box is null)
            {
                continue;
            }

            double west = Number(Child(box, "westBoundLongitude"));
            double east = Number(Child(box, "eastBoundLongitude"));
            double south = Number(Child(box, "southBoundLatitude"));
            double north = Number(Child(box, "northBoundLatitude"));

            const int Size = 101;

            // Walk a coarse grid rather than trusting the centre: a layer shaped like
            // a ring, or a diagonal of small squares, has nothing at its middle.
            for (int i = 1; i < Size; i += 10)
            {
                for (int j = 1; j < Size; j += 10)
                {
                    string url =
                        $"/wms?service=WMS&version=1.3.0&request=GetFeatureInfo"
                        + $"&layers={Uri.EscapeDataString(name)}"
                        + $"&query_layers={Uri.EscapeDataString(name)}&styles="
                        + $"&crs=CRS:84&bbox={west},{south},{east},{north}"
                        + $"&width={Size}&height={Size}&i={i}&j={j}"
                        + "&info_format=application/json";

                    (string mediaType, byte[] body) = await RawAsync(url);

                    // <b>A refusal is XML, and this is a search rather than an
                    // assertion.</b> Another suite may have left a service stopped or
                    // half-created between the capabilities document and this query,
                    // and a layer that refuses is simply not the layer being looked
                    // for. Parsing it as JSON made this fail for a reason that had
                    // nothing to do with what it tests.
                    if (!string.Equals(mediaType, "application/json", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    System.Text.Json.JsonElement document =
                        System.Text.Json.JsonDocument.Parse(body).RootElement;

                    if (document.TryGetProperty("features", out System.Text.Json.JsonElement features)
                        && features.GetArrayLength() > 0)
                    {
                        return url;
                    }
                }
            }
        }

        return null;
    }

    private static double Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : 0;
}
