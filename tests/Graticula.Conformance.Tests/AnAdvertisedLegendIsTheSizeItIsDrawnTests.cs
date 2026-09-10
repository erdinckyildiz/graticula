using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The box a WMS client reserves for a legend, against the picture it then receives.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-234](../../docs/architecture-debt.md).</b> The capabilities document wrote
/// <c>&lt;LegendURL width="20" height="20"&gt;</c> unconditionally while <c>LegendGraphic</c>
/// computed a size from the swatch and the widest label. A classified layer therefore
/// advertised 20×20 and served something else entirely — measured on the fixture at the time
/// as 105×68, and on this one as 106×488, which is the point: **the number depends on the
/// style, so no constant can be right for more than one of them.**
/// </para>
/// <para>
/// <b>Why the constant was ever true.</b> A single-swatch legend *is* the requested swatch, and
/// <c>WmsRequest.TryLegend</c> defaults <c>WIDTH</c> and <c>HEIGHT</c> to 20 — so the document
/// was correct for every style that existed when it was written and became wrong when
/// classified legends arrived. Two numbers decided in two places for two reasons, which is the
/// shape this repository keeps finding rather than a careless constant.
/// </para>
/// <para>
/// <b>This compares the two numbers rather than either one against an expectation</b>, which is
/// what makes it survive a style change. A test asserting <em>106×488</em> would have to be
/// edited every time somebody restyled the fixture, and would then be edited to whatever the
/// server said — which is how a test comes to assert the defect.
/// </para>
/// <para>
/// <b>Both kinds are covered by walking every layer.</b> The fixture publishes classified and
/// unclassified layers, and the unclassified ones are the case the old constant got right —
/// leaving them out would let a repair that broke them pass.
/// </para>
/// </remarks>
[Trait("Needs", "RunningHost")]
public sealed class AnAdvertisedLegendIsTheSizeItIsDrawnTests : ArcGisClient
{
    /// <summary>WMS 1.3.0's namespace, which the capabilities document is written in.</summary>
    private const string Wms = "http://www.opengis.net/wms";

    /// <summary>Every published layer's advertised legend against its drawn one.</summary>
    /// <returns>The task.</returns>
    [Fact]
    public async Task Every_layer_advertises_the_legend_it_actually_draws()
    {
        string root = await RequireServerAsync();

        using HttpResponseMessage answered = await Http.GetAsync(
            new Uri($"{root}/wms?service=WMS&version=1.3.0&request=GetCapabilities"));

        string body = await answered.Content.ReadAsStringAsync();

        Assert.True(
            answered.StatusCode == HttpStatusCode.OK,
            $"GetCapabilities answered {(int)answered.StatusCode}: {body}");

        XDocument document = XDocument.Parse(body);

        List<(string Name, int Width, int Height)> advertised = [];

        foreach (XElement layer in document.Descendants(XName.Get("Layer", Wms)))
        {
            XElement? name = layer.Element(XName.Get("Name", Wms));
            XElement? url = layer.Element(XName.Get("Style", Wms))
                ?.Element(XName.Get("LegendURL", Wms));

            if (name is null || url is null)
            {
                continue;
            }

            advertised.Add((
                name.Value,
                int.Parse(url.Attribute("width")!.Value, CultureInfo.InvariantCulture),
                int.Parse(url.Attribute("height")!.Value, CultureInfo.InvariantCulture)));
        }

        Assert.True(
            advertised.Count > 0,
            "No layer in the capabilities document carries a LegendURL, so this test is reading "
            + "nothing. Either WMS publishes nothing here or the element moved.");

        List<string> wrong = [];

        foreach ((string name, int width, int height) in advertised)
        {
            (int drawnWidth, int drawnHeight) = await LegendSizeAsync(name);

            if (drawnWidth != width || drawnHeight != height)
            {
                wrong.Add(
                    $"{name} advertises {width}x{height} and draws {drawnWidth}x{drawnHeight}");
            }
        }

        Assert.True(
            wrong.Count == 0,
            "A client reserves the advertised box for the image it fetches, so these layers lay "
            + "out a broken legend — worse for a desktop client composing a print layout from the "
            + "number than for a browser, which measures what it received:\n  "
            + string.Join("\n  ", wrong));
    }

    /// <summary>The size of the PNG this layer's legend endpoint actually returns.</summary>
    private async Task<(int Width, int Height)> LegendSizeAsync(string layer)
    {
        string root = await RequireServerAsync();

        using HttpResponseMessage response = await Http.GetAsync(new Uri(
            $"{root}/wms?service=WMS&version=1.3.0&request=GetLegendGraphic"
            + $"&layer={Uri.EscapeDataString(layer)}&format=image/png"));

        Assert.True(
            response.IsSuccessStatusCode,
            $"GetLegendGraphic for `{layer}` answered {(int)response.StatusCode}");

        byte[] png = await response.Content.ReadAsByteArrayAsync();

        // <b>The IHDR, read directly rather than through an image library.</b> A PNG's first
        // chunk is its header and its width and height are eight big-endian bytes at offset 16.
        // Decoding the whole image to learn two numbers would make this test depend on a codec
        // to check a document.
        Assert.True(
            png.Length > 24
                && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47,
            $"The legend for `{layer}` is not a PNG; it is {png.Length} bytes beginning "
            + string.Join(" ", png.Take(8).Select(b => b.ToString("X2", CultureInfo.InvariantCulture))));

        return (Read(png, 16), Read(png, 20));

        static int Read(byte[] png, int at) =>
            (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
    }
}
