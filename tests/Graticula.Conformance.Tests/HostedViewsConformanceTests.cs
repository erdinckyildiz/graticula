using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Hosted feature layer views — ADR-113 conditions 1 to 3: a view reads only its filter's rows, cannot edit past it,
/// adds into its source, and the refusals hold.
/// </summary>
/// <remarks>
/// <b>It creates what it changes</b>: a three-point source under a <c>zz_</c> name and two views of it, all deleted at
/// the end — views first, as the server insists.
/// </remarks>
[Collection("catalogue walk")]
public sealed class HostedViewsConformanceTests : ArcGisClient
{
    private const string Points =
        """{"type":"FeatureCollection","features":[""" +
        """{"type":"Feature","geometry":{"type":"Point","coordinates":[32.85,39.93]},"properties":{"kind":"alpha","n":1}},""" +
        """{"type":"Feature","geometry":{"type":"Point","coordinates":[32.86,39.94]},"properties":{"kind":"alpha","n":2}},""" +
        """{"type":"Feature","geometry":{"type":"Point","coordinates":[32.87,39.95]},"properties":{"kind":"bravo","n":3}}]}""";

    private static readonly string[] Editing = ["Query", "Update", "Delete"];
    private static readonly string[] AddOnly = ["Create"];

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        string root, string? token, HttpMethod method, string path, HttpContent? content = null)
    {
        using HttpRequestMessage request = new(method, $"{root}{path}") { Content = content };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    private async Task<JsonElement> QueryAsync(string root, string token, string service)
    {
        (HttpStatusCode status, string body) = await SendAsync(root, token, HttpMethod.Get,
            $"/rest/services/hosted/{service}/FeatureServer/0/query?where=1%3D1&outFields=*&orderByFields=objectid&f=json");
        Assert.True(status == HttpStatusCode.OK, $"Querying '{service}' answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static long[] Ids(JsonElement answer, string kind) =>
        [.. answer.GetProperty("features").EnumerateArray()
            .Where(f => f.GetProperty("attributes").GetProperty("kind").GetString() == kind)
            .Select(f => f.GetProperty("attributes").GetProperty("objectid").GetInt64())];

    /// <summary>The z/y/x of the tile over a point, as the tile URL spells it.</summary>
    private static string TileOver(double lon, double lat, int z)
    {
        double n = Math.Pow(2, z);
        int x = (int)((lon + 180) / 360 * n);
        double r = lat * Math.PI / 180;
        int y = (int)((1 - Math.Log(Math.Tan(r) + (1 / Math.Cos(r))) / Math.PI) / 2 * n);
        return FormattableString.Invariant($"{z}/{y}/{x}");
    }

    private async Task<byte[]> TileAsync(string root, string token, string service, string tile)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"{root}/rest/services/hosted/{service}/VectorTileServer/tile/{tile}.pbf");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"The tile {tile} of '{service}' answered {(int)response.StatusCode}.");
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static bool Holds(byte[] tile, string value) =>
        tile.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) >= 0;

    [Fact]
    public async Task A_view_reads_and_edits_only_its_rows_and_its_source_keeps_them()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string stem = Guid.NewGuid().ToString("N")[..8];
        string source = $"zz_src_{stem}";
        string onlyA = $"zz_va_{stem}";
        string addOnly = $"zz_vadd_{stem}";

        using MultipartFormDataContent upload = new();
        upload.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(Points)), "file", "points.geojson");
        upload.Add(new StringContent(source), "name");

        (HttpStatusCode made, string madeBody) = await SendAsync(root, token, HttpMethod.Post, "/admin/hosted/import", upload);
        Assert.True(made is HttpStatusCode.Created or HttpStatusCode.OK, $"The import failed: {(int)made} {madeBody}");

        try
        {
            long[] bs = Ids(await QueryAsync(root, token!, source), "bravo");

            // ------------------------------------------------------------ a filtered, editable view
            (HttpStatusCode viewed, string viewBody) = await SendAsync(root, token, HttpMethod.Post,
                $"/admin/services/{source}/views?folder=hosted",
                Json(new { name = onlyA, definitions = new Dictionary<string, string> { ["0"] = "kind = 'alpha'" }, capabilities = Editing }));
            Assert.True(viewed == HttpStatusCode.Created, $"Making the view answered {(int)viewed}: {viewBody}");

            JsonElement seen = await QueryAsync(root, token!, onlyA);
            Assert.Equal(2, seen.GetProperty("features").GetArrayLength());
            Assert.Empty(Ids(seen, "bravo"));

            (_, string countBody) = await SendAsync(root, token, HttpMethod.Get,
                $"/rest/services/hosted/{onlyA}/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json");
            Assert.Equal(2, JsonDocument.Parse(countBody).RootElement.GetProperty("count").GetInt32());

            // Tiles read the same PostgreSQL view: the source's tile holds the row the view's leaves out (condition 1).
            string tile = TileOver(32.86, 39.94, 10);
            byte[] sourceTile = await TileAsync(root, token!, source, tile);
            byte[] viewTile = await TileAsync(root, token!, onlyA, tile);
            Assert.True(Holds(sourceTile, "bravo"), "The source's tile does not hold the row the test expects to be filtered.");
            Assert.False(Holds(viewTile, "bravo"), "The view's tile holds a row its filter leaves out.");
            Assert.True(Holds(viewTile, "alpha"), "The view's tile holds none of its own rows.");

            // The documents say what each is.
            (_, string viewDoc) = await SendAsync(root, token, HttpMethod.Get, $"/rest/services/hosted/{onlyA}/FeatureServer?f=json");
            Assert.True(JsonDocument.Parse(viewDoc).RootElement.GetProperty("isView").GetBoolean(), viewDoc);
            (_, string sourceDoc) = await SendAsync(root, token, HttpMethod.Get, $"/rest/services/hosted/{source}/FeatureServer?f=json");
            Assert.True(JsonDocument.Parse(sourceDoc).RootElement.GetProperty("hasViews").GetBoolean(), sourceDoc);

            // An update past the filter reaches nothing; one inside it lands in the source.
            long a = Ids(seen, "alpha")[0];
            (_, string past) = await SendAsync(root, token, HttpMethod.Post, $"/rest/services/hosted/{onlyA}/FeatureServer/0/applyEdits",
                Form(("updates", $$$"""[{"attributes":{"objectid":{{{bs[0]}}},"n":99}}]"""), ("f", "json")));
            Assert.DoesNotContain("\"success\":true", past.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);

            (_, string inside) = await SendAsync(root, token, HttpMethod.Post, $"/rest/services/hosted/{onlyA}/FeatureServer/0/applyEdits",
                Form(("updates", $$$"""[{"attributes":{"objectid":{{{a}}},"n":42}}]"""), ("f", "json")));
            Assert.Contains("\"success\":true", inside.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);

            JsonElement after = await QueryAsync(root, token!, source);
            JsonElement[] rows = [.. after.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("attributes"))];
            Assert.Equal(42, rows.Single(r => r.GetProperty("objectid").GetInt64() == a).GetProperty("n").GetInt32());
            Assert.Equal(3, rows.Single(r => r.GetProperty("objectid").GetInt64() == bs[0]).GetProperty("n").GetInt32());

            // ------------------------------------------------------------ an add-only view
            (HttpStatusCode adder, string adderBody) = await SendAsync(root, token, HttpMethod.Post,
                $"/admin/services/{source}/views?folder=hosted", Json(new { name = addOnly, capabilities = AddOnly }));
            Assert.True(adder == HttpStatusCode.Created, $"Making the add-only view answered {(int)adder}: {adderBody}");

            (HttpStatusCode blind, _) = await SendAsync(root, token, HttpMethod.Get,
                $"/rest/services/hosted/{addOnly}/FeatureServer/0/query?where=1%3D1&f=json");
            Assert.NotEqual(HttpStatusCode.OK, blind);

            (_, string added) = await SendAsync(root, token, HttpMethod.Post, $"/rest/services/hosted/{addOnly}/FeatureServer/0/applyEdits",
                Form(("adds", """[{"geometry":{"x":32.9,"y":39.9,"spatialReference":{"wkid":4326}},"attributes":{"kind":"c","n":7}}]"""), ("f", "json")));
            Assert.Contains("\"success\":true", added.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.Equal(4, (await QueryAsync(root, token!, source)).GetProperty("features").GetArrayLength());

            // ------------------------------------------------------------ the refusals
            using MultipartFormDataContent more = new();
            more.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(Points)), "file", "points.geojson");
            (HttpStatusCode intoView, string intoViewBody) = await SendAsync(root, token, HttpMethod.Post, $"/admin/hosted/{onlyA}/append", more);
            Assert.True(intoView == HttpStatusCode.Conflict, $"Appending to a view answered {(int)intoView}: {intoViewBody}");

            using MultipartFormDataContent replacing = new();
            replacing.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(Points)), "file", "points.geojson");
            (HttpStatusCode overwrite, string overwriteBody) = await SendAsync(root, token, HttpMethod.Post, $"/admin/hosted/{source}/overwrite", replacing);
            Assert.True(overwrite == HttpStatusCode.Conflict, $"Overwriting a source with views answered {(int)overwrite}: {overwriteBody}");
            Assert.Contains(onlyA, overwriteBody, StringComparison.Ordinal);

            (HttpStatusCode early, string earlyBody) = await SendAsync(root, token, HttpMethod.Delete,
                $"/admin/featureservices/{source}?folder=hosted&drop=true");
            Assert.True(early == HttpStatusCode.Conflict, $"Deleting a source with views answered {(int)early}: {earlyBody}");

            // ------------------------------------------------------------ a view goes and its source's rows stay
            (HttpStatusCode gone, string goneBody) = await SendAsync(root, token, HttpMethod.Delete,
                $"/admin/featureservices/{onlyA}?folder=hosted&drop=true");
            Assert.True(gone == HttpStatusCode.OK, $"Deleting the view answered {(int)gone}: {goneBody}");
            Assert.Equal(4, (await QueryAsync(root, token!, source)).GetProperty("features").GetArrayLength());
        }
        finally
        {
            foreach (string name in new[] { onlyA, addOnly, source })
            {
                await SendAsync(root, token, HttpMethod.Delete, $"/admin/featureservices/{name}?folder=hosted&drop=true");
            }
        }
    }
}
