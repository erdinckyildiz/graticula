using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// One row with an elevation and a measure, read back through every surface that now carries them, and
/// written through <c>applyEdits</c> — ADR-077 condition 3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Through the running server, because that is what the condition asks.</b> The reader, the writers and the
/// PostGIS source each have tests of their own; what none of them can show is that the query each surface builds
/// asks for the ordinates, so the elevation reaches the wire. A surface that forgot `KeepOrdinates` passes every
/// one of those tests and answers flat.
/// </para>
/// <para>
/// <b>Over <c>cifree.zz_three_d</c></b>, which <c>tools/ci-free-tables.sql</c> creates as <c>PointZM</c> with one
/// row at 29, 41, elevation 120.5 and measure 7. Published and removed by this test, in the catalogue-walk
/// collection for the reason <c>EmptiedServiceTests</c> gives.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class AThreeDimensionalRowComesBackWholeTests : ArcGisClient
{
    private const string Layer = "zz_three_d_probe";
    private const string Service = "zz_three_d_svc";
    private const string Address = $"/rest/services/hosted/{Service}/FeatureServer/0";

    [Fact]
    public async Task The_elevation_reaches_every_surface_and_an_edit_keeps_it()
    {
        string? datastore = await DatastoreIdAsync();

        Assert.False(string.IsNullOrEmpty(datastore), "No datastore data source to publish the 3D fixture from.");

        (int published, string reason) = await AdminAsync(
            HttpMethod.Post,
            "/admin/layers",
            $$"""
            {"name":"{{Layer}}","dataSourceId":"{{datastore}}","schemaName":"cifree",
             "tableName":"zz_three_d","geometryColumn":"shape","geometryType":"Point",
             "identityColumn":"objectid","objectIdColumn":"objectid","srid":4326,"serviceName":"{{Service}}",
             "folder":"hosted"}
            """);

        Assert.True(
            published is 200 or 201,
            $"Could not publish cifree.zz_three_d: {published} {reason}. This test needs the PointZM table "
            + "tools/ci-free-tables.sql creates.");

        try
        {
            // ---- the layer document says what the column declares ----
            JsonElement document = await JsonAsync($"{Address}?f=json");
            Assert.True(document.GetProperty("hasZ").GetBoolean(), $"hasZ is false: {document}");
            Assert.True(document.GetProperty("hasM").GetBoolean(), $"hasM is false: {document}");

            // ---- ArcGIS query, json ----
            JsonElement answer = await JsonAsync(
                $"{Address}/query?where=1%3D1&outFields=*&returnZ=true&returnM=true&f=json");
            JsonElement point = answer.GetProperty("features")[0].GetProperty("geometry");
            Assert.Equal(120.5, point.GetProperty("z").GetDouble());
            Assert.Equal(7, point.GetProperty("m").GetDouble());

            // And not asked is flat, as it was before ADR-077.
            JsonElement flat = (await JsonAsync($"{Address}/query?where=1%3D1&f=json"))
                .GetProperty("features")[0].GetProperty("geometry");
            Assert.False(flat.TryGetProperty("z", out _), $"An unasked query returned z: {flat}");

            // ---- OGC API Features: the elevation as the third element ----
            JsonElement collections = await JsonAsync("/ogc/features/v1/collections?f=json");
            string collection = collections.GetProperty("collections").EnumerateArray()
                .Select(c => c.GetProperty("id").GetString()!)
                .First(id => id.Contains(Service, StringComparison.Ordinal) || id.Contains(Layer, StringComparison.Ordinal));

            JsonElement items = await JsonAsync(
                $"/ogc/features/v1/collections/{Uri.EscapeDataString(collection)}/items?f=json&limit=1");
            JsonElement coordinates = items.GetProperty("features")[0].GetProperty("geometry").GetProperty("coordinates");
            Assert.Equal(3, coordinates.GetArrayLength());
            Assert.Equal(120.5, coordinates[2].GetDouble());

            // ---- WFS: srsDimension 3 ----
            (int capabilitiesStatus, string capabilities) = await AdminAsync(
                HttpMethod.Get, "/wfs?service=WFS&version=2.0.0&request=GetCapabilities");
            Assert.Equal(200, capabilitiesStatus);

            XNamespace wfs = "http://www.opengis.net/wfs/2.0";
            string type = XDocument.Parse(capabilities).Descendants(wfs + "FeatureType")
                .Select(t => t.Element(wfs + "Name")!.Value)
                .First(name => name.Contains(Service, StringComparison.Ordinal) || name.Contains(Layer, StringComparison.Ordinal));

            (int gmlStatus, string gml) = await AdminAsync(
                HttpMethod.Get,
                $"/wfs?service=WFS&version=2.0.0&request=GetFeature&typeNames={Uri.EscapeDataString(type)}&count=1");
            Assert.Equal(200, gmlStatus);

            XElement pos = XDocument.Parse(gml).Descendants(XNamespace.Get("http://www.opengis.net/gml/3.2") + "pos").Single();
            Assert.Equal("3", (string?)pos.Attribute("srsDimension"));
            Assert.EndsWith(" 120.5", pos.Value, StringComparison.Ordinal);

            // ---- applyEdits: an elevation and a measure are stored, a flat point is refused ----
            JsonElement added = await ApplyEditsAsync(
                """[{"geometry":{"x":30,"y":42,"z":250.25,"m":9,"spatialReference":{"wkid":4326}},"attributes":{"name":"sent"}}]""");
            JsonElement result = added.GetProperty("addResults")[0];
            Assert.True(result.GetProperty("success").GetBoolean(), $"The 3D add was refused: {added}");
            long id = result.GetProperty("objectId").GetInt64();

            JsonElement back = (await JsonAsync(
                    $"{Address}/query?objectIds={id}&returnZ=true&returnM=true&f=json"))
                .GetProperty("features")[0].GetProperty("geometry");
            Assert.Equal(250.25, back.GetProperty("z").GetDouble());
            Assert.Equal(9, back.GetProperty("m").GetDouble());

            JsonElement refused = (await ApplyEditsAsync(
                    """[{"geometry":{"x":30,"y":42,"spatialReference":{"wkid":4326}},"attributes":{"name":"flat"}}]"""))
                .GetProperty("addResults")[0];
            Assert.False(refused.GetProperty("success").GetBoolean(), $"A flat point was stored in a PointZM column: {refused}");
            Assert.Contains("Z and M ordinates", refused.ToString(), StringComparison.Ordinal);

            await ApplyEditsAsync(null, deletes: id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/layers/{Layer}");
            await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{Service}?folder=hosted");
        }
    }

    private async Task<JsonElement> JsonAsync(string path)
    {
        (int status, string body) = await AdminAsync(HttpMethod.Get, path);
        Assert.True(status == 200, $"{path} answered {status}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<JsonElement> ApplyEditsAsync(string? adds, string? deletes = null)
    {
        string root = await RequireServerAsync();

        List<KeyValuePair<string, string>> fields = [new("f", "json")];

        if (adds is not null)
        {
            fields.Add(new("adds", adds));
        }

        if (deletes is not null)
        {
            fields.Add(new("deletes", deletes));
        }

        using FormUrlEncodedContent content = new(fields);
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{root}{Address}/applyEdits")) { Content = content };

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"applyEdits answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
