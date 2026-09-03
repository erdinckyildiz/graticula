using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A symbology preview draws the document it was handed, and keeps none of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owner request 2026-09-03: *bana gui editör lazım*.</b> The half of an appearance editor
/// that decides anything is the picture, and a picture is only useful if it is of the *candidate*
/// — the document on screen, not the one already stored. A preview that quietly drew the stored
/// appearance would look completely correct: it would show a map, it would show the right layer,
/// and it would never change.
/// </para>
/// <para>
/// <b>Asserted by comparing three answers rather than by decoding one.</b> The same candidate
/// twice must give the same bytes, and a different candidate must give different bytes — which
/// together say that the document is what the picture is a function of. Decoding the PNG and
/// naming the colour would be a second implementation of the renderer's own palette; what is in
/// question here is not *is crimson crimson*, it is *does the request reach the paint*.
/// </para>
/// <para>
/// <b>And it keeps nothing.</b> The endpoint is a `POST` because it carries a whole style
/// document, not because it writes; the layer's stored symbology is read before and after and has
/// to be the same text. A preview that stored what it drew would turn every keystroke in an
/// editor into a published appearance.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class SymbologyPreviewDrawsTheCandidateTests : ArcGisClient
{
    /// <summary>A `drawingInfo` with one solid fill, in whatever colour is asked for.</summary>
    /// <param name="red">Its red channel.</param>
    /// <param name="green">Its green channel.</param>
    /// <param name="blue">Its blue channel.</param>
    /// <returns>The document, as it would be pasted from ArcGIS.</returns>
    private static string Solid(int red, int green, int blue) =>
        System.String.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{{\"renderer\":{{\"type\":\"simple\",\"symbol\":"
            + $"{{\"type\":\"esriSFS\",\"style\":\"esriSFSSolid\","
            + $"\"color\":[{red},{green},{blue},255],"
            + $"\"outline\":{{\"type\":\"esriSLS\",\"style\":\"esriSLSSolid\","
            + $"\"color\":[0,0,0,255],\"width\":1}}}}}}}}");

    [Fact]
    public async Task The_picture_follows_the_candidate_document_and_nothing_is_stored()
    {
        string root = await RequireServerAsync();
        string layer = await AFillLayerAsync(root);

        string before = await StoredSymbologyAsync(root, layer);

        byte[] crimson = await PreviewAsync(root, layer, Solid(220, 20, 60));
        byte[] again = await PreviewAsync(root, layer, Solid(220, 20, 60));
        byte[] sea = await PreviewAsync(root, layer, Solid(46, 139, 87));

        Assert.True(
            crimson.Length > 100,
            $"The preview answered {crimson.Length} bytes, which is not a picture of anything.");

        Assert.True(
            crimson.AsSpan().SequenceEqual(again),
            "The same document drew two different pictures, so something other than the "
            + $"document is deciding what is painted ({crimson.Length} bytes, then "
            + $"{again.Length}).");

        Assert.False(
            crimson.AsSpan().SequenceEqual(sea),
            "Two different documents drew byte-identical pictures, so the candidate is not "
            + "reaching the renderer — which is what a preview showing the stored appearance "
            + "looks like from the outside, and it looks entirely correct.");

        // <b>Read back rather than assumed.</b> `no-store` on the response says nothing about
        // what the handler did to the catalogue.
        Assert.Equal(before, await StoredSymbologyAsync(root, layer));
    }

    [Fact]
    public async Task A_document_that_cannot_be_read_is_refused_in_a_sentence()
    {
        string root = await RequireServerAsync();
        string layer = await AFillLayerAsync(root);

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            new Uri($"{root}/admin/layers/{Uri.EscapeDataString(layer)}/symbology/preview"))
        {
            Content = new StringContent(
                """{"renderer":{"type":"tessellation"}}""",
                Encoding.UTF8,
                "application/json"),
        };

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);
        string said = await response.Content.ReadAsStringAsync();

        // <b>A status a form can act on, and a sentence it can print.</b> An editor that got a
        // broken image back would show an empty frame, which reads as *this style draws
        // nothing* rather than *this style could not be read*.
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"A renderer nothing can read answered {(int)response.StatusCode}: {said}");

        Assert.Contains("tessellation", said, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Asks for a picture of a candidate document.</summary>
    /// <param name="root">The server.</param>
    /// <param name="layer">The layer to draw.</param>
    /// <param name="candidate">The document to draw it with.</param>
    /// <returns>The PNG.</returns>
    private async Task<byte[]> PreviewAsync(string root, string layer, string candidate)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            new Uri($"{root}/admin/layers/{Uri.EscapeDataString(layer)}/symbology/preview"))
        {
            Content = new StringContent(candidate, Encoding.UTF8, "application/json"),
        };

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"The preview of '{layer}' answered {(int)response.StatusCode}: "
            + await response.Content.ReadAsStringAsync());

        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);

        // <b>`no-store`, and it is worth asserting.</b> The one thing a preview must never do is
        // hand somebody an earlier edit and let them keep it.
        Assert.Contains(
            "no-store",
            response.Headers.CacheControl?.ToString() ?? string.Empty,
            StringComparison.Ordinal);

        return await response.Content.ReadAsByteArrayAsync();
    }

    /// <summary>The document a layer has stored, as text, or `""` when it has none.</summary>
    /// <param name="root">The server.</param>
    /// <param name="layer">The layer.</param>
    /// <returns>The stored document.</returns>
    private async Task<string> StoredSymbologyAsync(string root, string layer)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            new Uri($"{root}/admin/layers/{Uri.EscapeDataString(layer)}/symbology"));

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Reading '{layer}' back answered {(int)response.StatusCode}.");

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        return document.RootElement.TryGetProperty("stored", out JsonElement stored)
            && stored.ValueKind == JsonValueKind.True
            && document.RootElement.TryGetProperty("symbology", out JsonElement had)
                ? had.GetRawText()
                : string.Empty;
    }

    /// <summary>
    /// A polygon layer, by name.
    /// </summary>
    /// <remarks>
    /// <b>Found rather than named.</b> A constant here would tie the test to one fixture and
    /// fail as a missing layer whenever the fixtures move, which is a failure about the suite
    /// rather than about the product. A fill is asked for because a solid fill is the symbol
    /// whose colour covers the most pixels, so a preview that ignored the candidate would still
    /// have to produce a byte-identical answer.
    /// </remarks>
    /// <param name="root">The server.</param>
    /// <returns>The layer's name.</returns>
    private async Task<string> AFillLayerAsync(string root)
    {
        foreach (string service in await EveryServiceNameAsync())
        {
            if (await AboutServiceAsync(service, $"/rest/services/{service}/FeatureServer")
                is not { } about
                || !about.TryGetProperty("layers", out JsonElement layers))
            {
                continue;
            }

            foreach (JsonElement layer in layers.EnumerateArray())
            {
                if (!layer.TryGetProperty("id", out JsonElement id))
                {
                    continue;
                }

                if (await AboutServiceAsync(
                        service, $"/rest/services/{service}/FeatureServer/{id.GetInt32()}")
                    is not { } described
                    || !described.TryGetProperty("geometryType", out JsonElement geometry)
                    || geometry.GetString() != "esriGeometryPolygon"
                    || !described.TryGetProperty("name", out JsonElement name))
                {
                    continue;
                }

                if (described.TryGetProperty("extent", out JsonElement extent)
                    && extent.ValueKind == JsonValueKind.Object
                    && name.GetString() is { Length: > 0 } found)
                {
                    return found;
                }
            }
        }

        Assert.Fail(
            $"No polygon layer with an extent is published on {root}, so there is nothing to "
            + "preview. This FAILS rather than skips: a conformance run with no drawable layer "
            + "is a run that proves nothing about drawing.");

        return string.Empty;
    }
}
