using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The operator's map ground is the portal's default basemap, named only to a caller who may draw it —
/// Q-110, ADR-086.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written 2026-09-24 with the setting.</b> The owner's answer to Q-110 was that the operator chooses the
/// ground and OpenStreetMap's tiles stay when nobody has. The console, the viewer and ArcGIS clients all read
/// it from <c>portals/self</c>, so that document is what is measured.
/// </para>
/// <para>
/// <b>In the catalogue-walk collection, because it publishes</b> (D-185), and the ground is put back in
/// <c>finally</c>: it is every map's on the fixture, as the page size is every query's.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class TheServerHasOneGroundTests : ArcGisClient
{
    private const string Open = "zz_ground_open";
    private const string Closed = "zz_ground_closed";

    private static readonly string[] Nowhere = ["zz_ground_nowhere"];

    private static List<string> Named(string body) =>
        JsonDocument.Parse(body).RootElement
            .GetProperty("defaultBasemap").GetProperty("baseMapLayers")
            .EnumerateArray()
            .Select(layer => layer.GetProperty("layerType").GetString() == "VectorTileLayer"
                ? layer.GetProperty("id").GetString()!
                : layer.GetProperty("layerType").GetString()!)
            .ToList();

    private async Task<string[]> GroundNowAsync()
    {
        (int status, string body) = await AdminAsync(HttpMethod.Get, "/admin/settings");
        Assert.Equal(200, status);

        return JsonDocument.Parse(body).RootElement
            .GetProperty("ground").GetProperty("services")
            .EnumerateArray()
            .Select(s => s.GetProperty("name").GetString()!)
            .ToArray();
    }

    [Fact]
    public async Task The_ground_is_named_to_whoever_may_draw_it_and_OpenStreetMap_to_everybody_else()
    {
        await RequireServerAsync();

        string[] before = await GroundNowAsync();

        try
        {
            (int open, string openBody) = await PublishOneAsync(Open, Open, sharing: "public", skip: 0);
            Assert.True(open is 200 or 201, $"publishing {Open}: {open} {openBody}");

            (int closed, string closedBody) = await PublishOneAsync(Closed, Closed, sharing: "private", skip: 1);
            Assert.True(closed is 200 or 201, $"publishing {Closed}: {closed} {closedBody}");

            (int set, string said) = await AdminAsync(
                HttpMethod.Put, "/admin/settings/ground",
                JsonSerializer.Serialize(new { services = new[] { Open, Closed } }));

            Assert.True(set == 200, $"setting the ground: {set} {said}");

            // The settings screen is told which one not everybody will see, so it can say so.
            Dictionary<string, bool> everybody = JsonDocument.Parse(said).RootElement
                .GetProperty("ground").GetProperty("services").EnumerateArray()
                .ToDictionary(s => s.GetProperty("name").GetString()!, s => s.GetProperty("everybody").GetBoolean());

            Assert.Equal([Open, Closed], everybody.Keys.ToArray());
            Assert.True(everybody[Open]);
            Assert.False(everybody[Closed]);

            // Bottom first, in the order chosen, to the administrator who may draw both.
            (int _, string mine) = await AdminAsync(HttpMethod.Get, "/sharing/rest/portals/self?f=json");
            Assert.Equal([Open, Closed], Named(mine));

            // An anonymous caller is told only about the one it can draw.
            (var anonymous, string theirs) = await AnonymousAsync("/sharing/rest/portals/self");
            Assert.Equal(System.Net.HttpStatusCode.OK, anonymous);
            Assert.Equal([Open], Named(theirs));

            // A name that is not a tile service is refused, and the ground stays as it was.
            (int refused, string why) = await AdminAsync(
                HttpMethod.Put, "/admin/settings/ground",
                JsonSerializer.Serialize(new { services = Nowhere }));

            Assert.Equal(400, refused);
            Assert.Contains("zz_ground_nowhere", why, StringComparison.Ordinal);
            Assert.Equal([Open, Closed], await GroundNowAsync());

            // Cleared, everybody is back on OpenStreetMap.
            (int cleared, _) = await AdminAsync(
                HttpMethod.Put, "/admin/settings/ground", JsonSerializer.Serialize(new { services = Array.Empty<string>() }));

            Assert.Equal(200, cleared);

            (_, string after) = await AnonymousAsync("/sharing/rest/portals/self");
            Assert.Equal(["OpenStreetMap"], Named(after));
        }
        finally
        {
            await AdminAsync(
                HttpMethod.Put, "/admin/settings/ground", JsonSerializer.Serialize(new { services = before }));

            await UnpublishAsync(Open, Open);
            await UnpublishAsync(Closed, Closed);
        }
    }
}
