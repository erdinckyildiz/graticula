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
/// <para>
/// <b>In the catalogue-walk collection, because this class walks the catalogue.</b>
/// xUnit runs test classes in parallel, and another class in this assembly publishes,
/// deletes and reconfigures services. A walker outside the collection sees the
/// catalogue mid-change and reports it as a defect in whatever it was testing —
/// [D-75](../../docs/architecture-debt.md), three times on 2026-08-20.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
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

    [Fact]
    public async Task A_drawn_map_uses_the_colour_the_tile_face_publishes()
    {
        // <b>[ADR-041](../../docs/adr/ADR-041-the-map-renderer.md) condition 3, and
        // it is [A-077](../../docs/architecture-assumptions.md)'s only evidence.</b>
        // ADR-033 stores one canonical symbology document per layer and derives two
        // faces from it: a MapLibre style for the tile service and, since 2026-08-20,
        // pixels for the map. Until this test the derivation's fidelity had been
        // asserted by reading it and tested by nothing.
        //
        // <b>Pixels rather than eyes.</b> The condition asked for a comparison by
        // eye; this compares the fill colour the tile face publishes with the fill
        // colour the renderer paints, which is the same question with an answer that
        // survives being run again.
        //
        // <b>Every candidate is tried, because a layer can be too small to see.</b>
        // The first version drew one layer at 128² over the whole country and found
        // no matching pixel — correctly: its fifty polygons are sub-pixel at that
        // scale and anti-alias away to nothing. A test that can fail for a reason
        // unrelated to what it asserts is a test that teaches its reader to ignore it.
        List<(string Layer, string Bbox, string Colour)> candidates = await FilledLayersAsync();

        Assert.True(
            candidates.Count > 0,
            "No published layer has a stored style with a constant fill colour, so this asserted "
            + "nothing about ADR-033's derivation.");

        List<string> misses = [];

        foreach ((string layer, string bbox, string colour) in candidates)
        {
            (string mediaType, byte[] image) = await RawAsync(
                MapUrl(layer, crs: "CRS:84", bbox: bbox, width: 768, height: 768,
                    extra: "&transparent=true"));

            Assert.Equal("image/png", mediaType);

            int matched = Painted(image, Hex(colour));

            if (matched > 0)
            {
                return;
            }

            misses.Add($"{layer} ({colour})");
        }

        Assert.Fail(
            "The tile face publishes a fill colour for each of these and the drawn map has no "
            + $"pixel of it in any: {string.Join(", ", misses)}. One canonical document, two "
            + "faces, two answers — a defect in ADR-033's derivation rather than in the renderer.");
    }

    /// <summary>How many pixels carry a colour, ignoring alpha.</summary>
    /// <remarks>
    /// <b>Alpha is not compared</b>: the style's own opacity folds into it, and what
    /// is being asserted is that the hue the tile face publishes is the hue drawn.
    /// Two shades of tolerance, because premultiplying and unpremultiplying a
    /// translucent colour through the encoder loses a least significant bit.
    /// </remarks>
    private static int Painted(byte[] png, (byte R, byte G, byte B) wanted)
    {
        using SkiaSharp.SKBitmap bitmap = SkiaSharp.SKBitmap.Decode(png);

        Assert.NotNull(bitmap);

        int matched = 0;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                SkiaSharp.SKColor pixel = bitmap.GetPixel(x, y);

                if (pixel.Alpha > 0
                    && Math.Abs(pixel.Red - wanted.R) <= 2
                    && Math.Abs(pixel.Green - wanted.G) <= 2
                    && Math.Abs(pixel.Blue - wanted.B) <= 2)
                {
                    matched++;
                }
            }
        }

        return matched;
    }

    /// <summary>Splits <c>#rrggbb</c> or <c>rgba(...)</c> into channels.</summary>
    private static (byte R, byte G, byte B) Hex(string colour)
    {
        if (colour.StartsWith('#') && colour.Length >= 7)
        {
            return (
                Convert.ToByte(colour.Substring(1, 2), 16),
                Convert.ToByte(colour.Substring(3, 2), 16),
                Convert.ToByte(colour.Substring(5, 2), 16));
        }

        string[] parts = colour[(colour.IndexOf('(', StringComparison.Ordinal) + 1)..]
            .TrimEnd(')')
            .Split(',');

        return (
            (byte)double.Parse(parts[0], CultureInfo.InvariantCulture),
            (byte)double.Parse(parts[1], CultureInfo.InvariantCulture),
            (byte)double.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Every layer whose tile-face style declares one constant fill colour.
    /// </summary>
    /// <remarks>
    /// <b>Constant, because a <c>match</c> over a column has no single answer</b> and
    /// comparing against its fallback would assert that the fallback is what draws —
    /// only true where the data has no matching value.
    /// </remarks>
    private async Task<List<(string Layer, string Bbox, string Colour)>> FilledLayersAsync()
    {
        List<(string Layer, string Bbox, string Colour)> found = [];
        IReadOnlyList<XElement> published = await PublishedAsync();

        foreach (string service in await EveryServiceNameAsync())
        {
            System.Text.Json.JsonElement style;

            try
            {
                style = await GetJsonAsync(
                    $"/rest/services/{service}/VectorTileServer/resources/styles/root.json");
            }
            catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException
                or Xunit.Sdk.XunitException)
            {
                // A registered service has no tile face, and GetJsonAsync fails a
                // test rather than returning null when a document 400s. This is a
                // search across every service; a service without the document is
                // simply not one of the candidates.
                continue;
            }

            if (!style.TryGetProperty("layers", out System.Text.Json.JsonElement layers))
            {
                continue;
            }

            foreach (System.Text.Json.JsonElement entry in layers.EnumerateArray())
            {
                if (!string.Equals(
                        entry.GetProperty("type").GetString(), "fill", StringComparison.Ordinal)
                    || !entry.TryGetProperty("paint", out System.Text.Json.JsonElement paint)
                    || !paint.TryGetProperty("fill-color", out System.Text.Json.JsonElement colour)
                    || colour.ValueKind != System.Text.Json.JsonValueKind.String)
                {
                    continue;
                }

                string name = entry.TryGetProperty(
                    "source-layer", out System.Text.Json.JsonElement source)
                        ? source.GetString()!
                        : entry.GetProperty("id").GetString()!;

                foreach (XElement layer in published)
                {
                    if (!string.Equals(Child(layer, "Name"), name, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    XElement? box = layer.Elements()
                        .FirstOrDefault(e => e.Name.LocalName == "EX_GeographicBoundingBox");

                    if (box is null)
                    {
                        continue;
                    }

                    found.Add((
                        name,
                        string.Join(
                            ',',
                            Child(box, "westBoundLongitude"),
                            Child(box, "southBoundLatitude"),
                            Child(box, "eastBoundLongitude"),
                            Child(box, "northBoundLatitude")),
                        colour.GetString()!));
                }
            }
        }

        return found;
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

    /// <summary>
    /// A layer whose stored style classifies gets a legend with a row per class.
    /// </summary>
    /// <remarks>
    /// <b>The end-to-end half of [Q-131](../../docs/open-questions.md), and the half
    /// the unit tests cannot claim.</b> They compile a style string in memory; this
    /// one goes through the store: a `uniqueValue` renderer was PUT at seed time,
    /// converted to the canonical document, kept, compiled on the request and drawn.
    /// A defect anywhere along that chain shows up here and nowhere else.
    /// </remarks>
    [Fact]
    public async Task A_classified_style_makes_a_legend_taller_than_one_swatch()
    {
        string? layer = await NamedPublishedAsync("_many");

        // Not a silent pass: the seed is supposed to publish this layer, and a suite
        // that shrugs when its fixture is missing reports green on nothing.
        Assert.NotNull(layer);

        (_, byte[] image) = await RawAsync(
            $"/wms?service=WMS&version=1.3.0&request=GetLegendGraphic"
            + $"&layer={Uri.EscapeDataString(layer!)}&format=image/png&width=20&height=20");

        // The PNG header carries the dimensions: width at byte 16, height at 20, both
        // big-endian. Reading them beats decoding the image for one number.
        int width = (image[16] << 24) | (image[17] << 16) | (image[18] << 8) | image[19];
        int height = (image[20] << 24) | (image[21] << 16) | (image[22] << 8) | image[23];

        Assert.True(
            height >= 60,
            $"Three classes should be three rows; the legend is {height} pixels tall.");

        Assert.True(
            width > 20,
            $"A classified legend needs room for its labels; this one is {width} wide.");
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

    // <b>[Q-130](../../docs/open-questions.md): a list and a periodicity are refused, and
    // `InvalidDimensionValue` is what WMS 1.3.0 says to refuse them with.</b> `GetMap`
    // returns one map, and the capabilities document advertises an interval with a
    // resolution rather than an enumeration — so a client sending either is asking for
    // something this server never offered. Answering with the first value would draw a
    // map of a moment nobody asked about, which looks exactly like the one they did.
    [InlineData("time=2024-01-01T00:00:00Z,2024-02-01T00:00:00Z", "InvalidDimensionValue")]
    [InlineData("time=2024-01-01T00:00:00Z/2024-12-01T00:00:00Z/P1D", "InvalidDimensionValue")]
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

    /// <summary>The first published layer whose name ends the way the seed names it.</summary>
    /// <remarks>
    /// <b>By suffix rather than by full name.</b> The seed prefixes every layer with
    /// a configurable string, so a test that hard-codes the whole name is a test about
    /// one machine's environment variables.
    /// </remarks>
    /// <param name="suffix">What the seeded layer's name ends with.</param>
    /// <returns>The layer's WMS name, or null when the seed did not publish it.</returns>
    private async Task<string?> NamedPublishedAsync(string suffix)
    {
        foreach (XElement layer in await PublishedAsync())
        {
            if (Child(layer, "Name") is { Length: > 0 } name
                && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return name;
            }
        }

        return null;
    }

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
