using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The map viewer draws a vector tile style's expressions — a <c>match</c>, a <c>step</c>, an <c>interpolate</c> —
/// in the colours they choose, not in black.
/// </summary>
/// <remarks>
/// <b>Written 2026-10-15 from the owner's screenshot of the showcase.</b> The map ground's districts and places are
/// styled by one <c>match</c> each — the places over two fields joined with <c>concat</c> — and the viewer read only
/// constants and <c>stops</c>, so both were drawn black while the Maps SDK drew them in their classes. The style
/// function is called on features built in the page, because what is under test is how a style is read, and no
/// fixture service is styled with every operator.
/// </remarks>
public sealed class WebMapDrawsStyleExpressionsTests : ConsoleTest
{
    private const string Drawn = """
        (() => {
          const style = { layers: [
            { id: "places", type: "circle", "source-layer": "places", paint: {
              "circle-color": ["match", ["concat", ["to-string", ["get", "ad"]], ", ", ["to-string", ["get", "nufus"]]],
                "Esenyurt, 795010", "#66ccee", "Ortaköy, ", "#228833", "#aaaaaa"],
              "circle-radius": 5 } },
            { id: "districts", type: "line", "source-layer": "districts", paint: {
              "line-color": ["match", ["get", "ilce"], ["Marmaris", "Bodrum"], "#4477aa", "#010101"],
              "line-width": ["interpolate", ["linear"], ["zoom"], 0, 1, 10, 3] } },
            { id: "land", type: "fill", "source-layer": "land", paint: {
              "fill-color": ["step", ["get", "n"], "#ff0000", 10, "#00ff00"] } },
          ] };
          const draw = wmGlStyle(style);
          const atFive = 156543.03392804097 / 2 ** 5;
          const first = props => draw(new ol.Feature(props), atFive)[0];
          const fill = props => Array.from(first(props).getImage().getFill().getColor()).join(",");
          const line = props => first(props).getStroke();
          return JSON.stringify({
            esenyurt: fill({ layer: "places", ad: "Esenyurt", nufus: 795010 }),
            ortakoy: fill({ layer: "places", ad: "Ortaköy" }),
            other: fill({ layer: "places", ad: "Elsewhere", nufus: 1 }),
            bodrum: Array.from(line({ layer: "districts", ilce: "Bodrum" }).getColor()).join(","),
            otherDistrict: Array.from(line({ layer: "districts", ilce: "Kaş" }).getColor()).join(","),
            width: line({ layer: "districts", ilce: "Kaş" }).getWidth(),
            many: Array.from(first({ layer: "land", n: 12 }).getFill().getColor()).join(","),
            few: Array.from(first({ layer: "land", n: 3 }).getFill().getColor()).join(","),
          });
        })()
        """;

    [Fact]
    public async Task A_match_a_step_and_an_interpolate_are_drawn_in_the_colours_they_choose()
    {
        (string token, string cookie) = await SignInAsync();

        await OpenAsync("/studio/webmap.html", token, cookie);
        await WaitForAsync("typeof wmGlStyle === 'function' && typeof ol !== 'undefined'", "The map viewer did not load.");

        using JsonDocument drawn = JsonDocument.Parse(await Browser.EvaluateAsync<string>(Drawn) ?? "{}");
        JsonElement root = drawn.RootElement;

        // Two fields joined, and one of them missing — `to-string` of nothing is "", as the specification says.
        Assert.Equal("102,204,238,1", root.GetProperty("esenyurt").GetString());
        Assert.Equal("34,136,51,1", root.GetProperty("ortakoy").GetString());
        Assert.Equal("170,170,170,1", root.GetProperty("other").GetString());

        // A label list, the fallback, and a width that follows the zoom: 1 at 0 and 3 at 10 is 2 at 5.
        Assert.Equal("68,119,170,1", root.GetProperty("bodrum").GetString());
        Assert.Equal("1,1,1,1", root.GetProperty("otherDistrict").GetString());
        Assert.Equal(2, root.GetProperty("width").GetDouble(), 3);

        Assert.Equal("0,255,0,1", root.GetProperty("many").GetString());
        Assert.Equal("255,0,0,1", root.GetProperty("few").GetString());
    }
}
