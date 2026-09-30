using System;
using System.IO;
using System.IO.Compression;
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
/// A layer taken away as a GeoPackage, a zipped shapefile or a workbook — ADR-107, condition 1.
/// </summary>
/// <remarks>
/// <b>Read by what each file is, not by the status alone.</b> A GeoPackage is an SQLite database and says so in its
/// first sixteen bytes; a zipped shapefile holds a .shp, a .dbf and the .prj that carries the layer's reference; a
/// workbook is a zip with an Office part list. A 200 with an error page as its body passes none of them.
/// </remarks>
[Collection("catalogue walk")]
public sealed class LayerExportConformanceTests : ArcGisClient
{
    private const string MultilayerVariable = "GRATICULA_TEST_MULTILAYER";

    private async Task<(HttpStatusCode Status, string? Type, byte[] Body)> ExportAsync(string root, string? token, string format)
    {
        string named = Environment.GetEnvironmentVariable(MultilayerVariable) ?? "hosted/ci_EarlyAlert";
        string[] parts = named.Split('/');
        string folder = parts.Length > 1 ? parts[0] : "";
        string service = parts[^1];

        using HttpRequestMessage request = new(HttpMethod.Post,
            $"{root}/admin/services/{service}/layers/0/export?folder={folder}&format={format}");

        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_layer_leaves_as_a_GeoPackage_a_zipped_shapefile_and_a_workbook()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        (HttpStatusCode gpkg, string? gpkgType, byte[] package) = await ExportAsync(root, token, "gpkg");

        Assert.Equal(HttpStatusCode.OK, gpkg);
        Assert.Equal("application/geopackage+sqlite3", gpkgType);
        Assert.StartsWith("SQLite format 3", Encoding.ASCII.GetString(package, 0, 15), StringComparison.Ordinal);

        (HttpStatusCode shp, string? shpType, byte[] zipped) = await ExportAsync(root, token, "shapefile");

        Assert.Equal(HttpStatusCode.OK, shp);
        Assert.Equal("application/zip", shpType);

        using (ZipArchive archive = new(new MemoryStream(zipped)))
        {
            string[] names = [.. archive.Entries.Select(e => Path.GetExtension(e.Name).ToLowerInvariant())];

            Assert.Contains(".shp", names);
            Assert.Contains(".dbf", names);
            Assert.Contains(".prj", names);
        }

        (HttpStatusCode xlsx, _, byte[] book) = await ExportAsync(root, token, "xlsx");

        Assert.Equal(HttpStatusCode.OK, xlsx);

        using (ZipArchive archive = new(new MemoryStream(book)))
        {
            Assert.Contains(archive.Entries, e => e.FullName == "[Content_Types].xml");
        }

        // ADR-106's formats on the same road. A File Geodatabase is a zipped .gdb folder, the folder itself inside.
        (HttpStatusCode fgdb, string? fgdbType, byte[] gdb) = await ExportAsync(root, token, "fgdb");

        Assert.Equal(HttpStatusCode.OK, fgdb);
        Assert.Equal("application/zip", fgdbType);

        using (ZipArchive archive = new(new MemoryStream(gdb)))
        {
            Assert.All(archive.Entries, e => Assert.Contains(".gdb/", e.FullName, StringComparison.Ordinal));
            Assert.Contains(archive.Entries, e => e.Name.EndsWith(".gdbtable", StringComparison.Ordinal));
        }

        (HttpStatusCode kml, string? kmlType, byte[] placemarks) = await ExportAsync(root, token, "kml");

        Assert.Equal(HttpStatusCode.OK, kml);
        Assert.Equal("application/vnd.google-earth.kml+xml", kmlType);
        Assert.Contains("<kml", Encoding.UTF8.GetString(placemarks), StringComparison.Ordinal);

        // CSV opens in Excel as UTF-8 because it starts with a byte-order mark; its coordinates are WGS 84.
        (HttpStatusCode csv, _, byte[] table) = await ExportAsync(root, token, "csv");

        Assert.Equal(HttpStatusCode.OK, csv);
        Assert.Equal([0xEF, 0xBB, 0xBF], table.Take(3));
        string header = Encoding.UTF8.GetString(table, 3, table.Length - 3).Split('\n')[0];
        Assert.True(header.Contains("WKT", StringComparison.Ordinal) || header.StartsWith("X,Y", StringComparison.Ordinal),
            $"The CSV's header carries no geometry: {header}");

        (HttpStatusCode geojson, string? geojsonType, byte[] collection) = await ExportAsync(root, token, "geojson");

        Assert.Equal(HttpStatusCode.OK, geojson);
        Assert.Equal("application/geo+json", geojsonType);

        using (JsonDocument parsed = JsonDocument.Parse(collection))
        {
            Assert.Equal("FeatureCollection", parsed.RootElement.GetProperty("type").GetString());
            JsonElement first = parsed.RootElement.GetProperty("features")[0].GetProperty("geometry").GetProperty("coordinates");

            while (first.ValueKind == JsonValueKind.Array && first[0].ValueKind == JsonValueKind.Array)
            {
                first = first[0];
            }

            Assert.InRange(first[0].GetDouble(), -180, 180);
            Assert.InRange(first[1].GetDouble(), -90, 90);
        }

        (HttpStatusCode unknown, _, _) = await ExportAsync(root, token, "dxf");

        Assert.Equal(HttpStatusCode.BadRequest, unknown);

        // Nobody anonymous, ever (owner decision 2026-10-01).
        (HttpStatusCode anonymous, _, _) = await ExportAsync(root, null, "gpkg");

        Assert.True(anonymous is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound or HttpStatusCode.Forbidden,
            $"An anonymous export answered {(int)anonymous}.");

        // Not a GET: an image tag must not be able to start one (ADR-098 §5.7).
        string named = Environment.GetEnvironmentVariable(MultilayerVariable) ?? "hosted/ci_EarlyAlert";
        using HttpRequestMessage get = new(HttpMethod.Get,
            $"{root}/admin/services/{named.Split('/')[^1]}/layers/0/export?folder={named.Split('/')[0]}&format=gpkg");
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage got = await Http.SendAsync(get);

        Assert.NotEqual(HttpStatusCode.OK, got.StatusCode);
    }
}
