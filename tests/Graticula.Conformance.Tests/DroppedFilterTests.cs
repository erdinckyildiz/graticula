using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A filter this server cannot evaluate is refused, and never quietly dropped.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-125](../../docs/architecture-debt.md), found by the second security gate.</b>
/// <c>MapServer/export</c> and <c>MapServer/identify</c> took <c>layerDefs</c> and dropped it:
/// three exports were byte-identical with no <c>layerDefs</c>, with <c>il='Adana'</c>, and with
/// <c>1=1; DROP--</c>. Nothing reached the database, which is why the gate rated it low — and is
/// also what makes it worth refusing. A caller filtering to hide something was shown everything,
/// and nothing in the answer said so.
/// </para>
/// <para>
/// <b>The rest of this server already behaves this way.</b> WFS's <c>FilterReader</c>, the OGC
/// face and <c>PortalQuery</c> all refuse a filter they cannot evaluate. This is the face that did
/// not.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class DroppedFilterTests : ArcGisClient
{
    /// <summary>
    /// A service with an extent worth drawing, and the extent to ask for.
    /// </summary>
    /// <remarks>
    /// <b>The same walk `MapServerConformanceTests` does, and for the same reason.</b> Not every
    /// service can be drawn — a service with no layers has no extent — so a test that asked the
    /// first name in the catalogue would report a missing layer as a missing refusal.
    /// </remarks>
    private async Task<(string Service, string Bbox, int Srid)?> DrawableAsync()
    {
        foreach (string service in await EveryServiceNameAsync())
        {
            JsonElement document;

            try
            {
                document = await GetJsonAsync($"/rest/services/{service}/MapServer");
            }
            catch (Exception e) when (e is HttpRequestException or JsonException)
            {
                continue;
            }

            if (!document.TryGetProperty("fullExtent", out JsonElement extent)
                || extent.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            double minX = extent.GetProperty("xmin").GetDouble();
            double minY = extent.GetProperty("ymin").GetDouble();
            double maxX = extent.GetProperty("xmax").GetDouble();
            double maxY = extent.GetProperty("ymax").GetDouble();

            if (maxX <= minX || maxY <= minY)
            {
                continue;
            }

            int srid = extent.GetProperty("spatialReference").GetProperty("wkid").GetInt32();

            string bbox = string.Join(
                ',',
                new[] { minX, minY, maxX, maxY }.Select(
                    n => n.ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture)));

            return (service, bbox, srid);
        }

        return null;
    }

    /// <summary>Asks with the suite's credential, the way every drawing test does.</summary>
    /// <remarks>
    /// <b>Because the catalogue this walks is the signed-in one.</b> `EveryServiceNameAsync` reads
    /// it with a token and finds private services; an unauthenticated draw of one answers *no
    /// layer is visible to you*, which reads exactly like the refusal under test. Found by this
    /// test failing on a private import before it had asserted anything.
    /// </remarks>
    private async Task<(int Status, string Body)> AskAsync(string url)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(url));
        await AuthenticateAsync(request, await RequireServerAsync());

        using HttpResponseMessage response = await Http.SendAsync(request);

        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Export refuses a `layerDefs` somebody wrote, and says why.
    /// </summary>
    /// <remarks>
    /// <b>The answer is a 200 carrying <c>error.code</c>, which is inherited rather than
    /// chosen.</b> Every Esri client reads the code out of a successful response and several treat
    /// a 4xx as a transport failure and never open the body — the same reasoning WMS service
    /// exceptions are written under.
    /// </remarks>
    [Fact]
    public async Task Export_refuses_a_layer_definition_it_cannot_evaluate()
    {
        string root = await RequireServerAsync();

        (string Service, string Bbox, int Srid)? drawable = await DrawableAsync();

        Assert.False(drawable is null, "No service in this catalogue can be drawn.");

        (string service, string bbox, int srid) = drawable!.Value;

        string map = $"{root}/rest/services/{service}/MapServer/export"
            + $"?bbox={bbox}&bboxSR={srid}&size=200,150&format=png&f=image";

        (int plain, string drawn) = await AskAsync(map);

        Assert.True(
            plain is 200 && !drawn.Contains("\"error\"", StringComparison.Ordinal),
            $"The export without a filter answered {plain} for {map}: {drawn[..Math.Min(200, drawn.Length)]}");

        (int _, string body) = await AskAsync(
            map + "&layerDefs=" + Uri.EscapeDataString("0:il='Adana'"));

        Assert.True(
            body.Contains("layerDefs", StringComparison.Ordinal),
            "The export accepted a layerDefs and drew a map. That is D-125: the caller asked for "
            + "some features and was shown all of them, with nothing in the answer saying so. "
            + $"It answered {body.Length} bytes.");

        JsonElement error = JsonDocument.Parse(body).RootElement.GetProperty("error");

        Assert.Equal(400, error.GetProperty("code").GetInt32());

        // The refusal has to leave the caller somewhere to go, or it is a wall rather than an
        // answer — the FeatureServer face evaluates `where` against the database.
        Assert.Contains(
            "FeatureServer",
            error.GetProperty("message").GetString() ?? string.Empty,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Identify refuses it too, because it took it and dropped it in the same way.
    /// </summary>
    [Fact]
    public async Task Identify_refuses_a_layer_definition_it_cannot_evaluate()
    {
        string root = await RequireServerAsync();

        (string Service, string Bbox, int Srid)? drawable = await DrawableAsync();

        Assert.False(drawable is null, "No service in this catalogue can be drawn.");

        (string service, string bbox, int srid) = drawable!.Value;

        // The middle of the extent, so the probe is inside the data rather than beside it.
        string[] corners = bbox.Split(',');
        double x = (double.Parse(corners[0], System.Globalization.CultureInfo.InvariantCulture)
            + double.Parse(corners[2], System.Globalization.CultureInfo.InvariantCulture)) / 2;
        double y = (double.Parse(corners[1], System.Globalization.CultureInfo.InvariantCulture)
            + double.Parse(corners[3], System.Globalization.CultureInfo.InvariantCulture)) / 2;

        string ask = $"{root}/rest/services/{service}/MapServer/identify"
            + $"?geometry={x.ToString(System.Globalization.CultureInfo.InvariantCulture)},"
            + $"{y.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + $"&geometryType=esriGeometryPoint&mapExtent={bbox}&sr={srid}"
            + "&imageDisplay=200,150,96&tolerance=5&f=json";

        (int _, string plain) = await AskAsync(ask);

        Assert.DoesNotContain("\"error\"", plain, StringComparison.Ordinal);

        (int _, string filtered) = await AskAsync(
            ask + "&layerDefs=" + Uri.EscapeDataString("0:il='Adana'"));

        JsonElement root2 = JsonDocument.Parse(filtered).RootElement;

        Assert.True(
            root2.TryGetProperty("error", out JsonElement error),
            "Identify accepted a layerDefs and answered results. That is D-125.");

        Assert.Equal(400, error.GetProperty("code").GetInt32());
    }

    /// <summary>
    /// An empty `layerDefs` is not a filter and is not refused.
    /// </summary>
    /// <remarks>
    /// <b>Because a client that builds its query string from a form writes `layerDefs=` for a
    /// filter nobody typed.</b> Refusing that would refuse the request every ArcGIS client makes
    /// by default, which turns a repair into an outage. What is refused is a filter somebody
    /// wrote.
    /// </remarks>
    [Fact]
    public async Task An_empty_layer_definition_is_not_a_filter_and_is_allowed()
    {
        string root = await RequireServerAsync();

        (string Service, string Bbox, int Srid)? drawable = await DrawableAsync();

        Assert.False(drawable is null, "No service in this catalogue can be drawn.");

        (string service, string bbox, int srid) = drawable!.Value;

        (int status, string answered) = await AskAsync(
            $"{root}/rest/services/{service}/MapServer/export"
            + $"?bbox={bbox}&bboxSR={srid}&size=200,150&format=png&f=image&layerDefs=");

        Assert.True(
            status is 200,
            $"An empty layerDefs was refused with {status}. Every ArcGIS client that builds a "
            + "query string from a form sends one.");

        Assert.DoesNotContain("layerDefs", answered, StringComparison.Ordinal);
    }
}
