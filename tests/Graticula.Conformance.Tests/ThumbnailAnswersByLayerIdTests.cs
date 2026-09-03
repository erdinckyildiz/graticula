using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A layer's picture is found by the number in its URL, not by its place in a list.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found on 2026-09-03 by a design review of the console, and it is a server defect.</b>
/// `/admin/thumbnail?service=…&amp;layer=N` read `N` as a position in the service's runtime layer
/// list; every caller means the ArcGIS layer id. `PublishedLayer` documents the difference
/// itself — *its number within that service — the `{id}` in the URL. Assigned once and never
/// reused. Gaps in the sequence are correct* — and a **group layer takes an id and is not
/// drawable**, so the two stop agreeing on exactly the services that have one.
/// </para>
/// <para>
/// <b>Both halves were measured before the repair, and the second is the worse one.</b> On a
/// service whose ids are 0, 1, 2 (a group) and 3: asking for layer 3 indexed past the end and
/// answered <b>404</b>, and asking for the group's id 2 answered <b>200 with the third drawable
/// layer's picture</b>. A missing picture is visible; a confident wrong one is not.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class ThumbnailAnswersByLayerIdTests : ArcGisClient
{
    [Fact]
    public async Task Every_published_id_answers_and_a_group_layer_has_no_picture_of_its_own()
    {
        string? qualified = Environment.GetEnvironmentVariable(
            MultiLayerServiceConformanceTests.GroupedVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(qualified),
            $"{MultiLayerServiceConformanceTests.GroupedVariable} is not set, so this test FAILS "
            + "rather than skips. It needs a service that has a group layer, because a group is "
            + "what makes an id stop being a position.");

        string root = await RequireServerAsync();
        string service = qualified!.Trim('/');

        JsonElement about = await GetJsonAsync(
            $"/rest/services/{service}/FeatureServer");

        List<int> drawable = [];
        List<int> groups = [];

        foreach (JsonElement layer in about.GetProperty("layers").EnumerateArray())
        {
            if (!layer.TryGetProperty("id", out JsonElement id))
            {
                continue;
            }

            bool group = layer.TryGetProperty("type", out JsonElement kind)
                && (kind.GetString() ?? string.Empty)
                    .Contains("Group", StringComparison.OrdinalIgnoreCase);

            (group ? groups : drawable).Add(id.GetInt32());
        }

        Assert.NotEmpty(groups);

        // <b>The gap is the point.</b> A service whose ids run 0..n-1 with no group cannot tell
        // a position from an id, and would have passed this test before the repair.
        Assert.Contains(
            drawable,
            id => id >= drawable.Count);

        foreach (int id in drawable)
        {
            (HttpStatusCode status, int bytes) = await PictureAsync(root, service, id);

            Assert.True(
                status == HttpStatusCode.OK && bytes > 100,
                $"Layer {id} of '{service}' is drawable and its picture answered {(int)status} "
                + $"with {bytes} bytes. Before this was repaired, every id past the count of "
                + "drawable layers answered 404 — which is every layer after a group.");
        }

        foreach (int id in groups)
        {
            (HttpStatusCode status, int bytes) = await PictureAsync(root, service, id);

            Assert.True(
                status == HttpStatusCode.NotFound,
                $"A group layer ({id}) answered {(int)status} with {bytes} bytes. A group draws "
                + "nothing of its own, so a picture for it is somebody else's — which is what "
                + "this used to serve, with a 200.");
        }
    }

    /// <summary>Asks for one layer's picture.</summary>
    /// <param name="root">The server.</param>
    /// <param name="service">The qualified service.</param>
    /// <param name="id">The layer id, as the service document publishes it.</param>
    /// <returns>What came back.</returns>
    private async Task<(HttpStatusCode Status, int Bytes)> PictureAsync(
        string root, string service, int id)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            new Uri($"{root}/admin/thumbnail?service={Uri.EscapeDataString(service)}&layer={id}"));

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, (await response.Content.ReadAsByteArrayAsync()).Length);
    }
}
