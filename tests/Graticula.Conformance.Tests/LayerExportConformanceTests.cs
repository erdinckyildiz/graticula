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

    /// <summary>
    /// A field's domain leaves this route's GeoPackage and File Geodatabase as a domain, as it leaves ADR-106's job's.
    /// </summary>
    /// <remarks>
    /// <b>Found 2026-10-14 by ArcGIS Pro.</b> This route never passed the layer's domains to the reader, so its files
    /// carried plain columns while the job's carried the lists. Pro listed no domain in this route's File Geodatabase,
    /// and the GeoPackage had no <c>gpkg_data_column_constraints</c> at all. Both files are searched by their bytes,
    /// as the job's test searches its own; the File Geodatabase's are inside the zip.
    /// </remarks>
    [Fact]
    public async Task A_fields_domain_leaves_this_routes_GeoPackage_and_File_Geodatabase_as_a_domain()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string named = Environment.GetEnvironmentVariable(MultilayerVariable) ?? "hosted/ci_EarlyAlert";

        using HttpRequestMessage ask = new(HttpMethod.Get, $"{root}/rest/services/{named}/FeatureServer/0?f=json");
        ask.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage described = await Http.SendAsync(ask);
        string layer = JsonDocument.Parse(await described.Content.ReadAsStringAsync()).RootElement.GetProperty("name").GetString()!;

        async Task<HttpStatusCode> FieldsAsync(string body)
        {
            using HttpRequestMessage put = new(HttpMethod.Put, $"{root}/admin/layers/{layer}/fields")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage answered = await Http.SendAsync(put);
            return answered.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await FieldsAsync("""
            {"overrides":[{"column":"severity","domain":{"type":"codedValue","name":"RouteSeverity",
             "codedValues":[{"code":0,"name":"Sakin ğ"},{"code":1,"name":"Dikkat ş"},{"code":2,"name":"Uyarı İ"},{"code":3,"name":"Alarm ı"}]}}]}
            """));

        try
        {
            (HttpStatusCode gpkg, _, byte[] package) = await ExportAsync(root, token, "gpkg");

            Assert.Equal(HttpStatusCode.OK, gpkg);

            string pages = Encoding.UTF8.GetString(package);

            Assert.Contains("gpkg_data_column_constraints", pages, StringComparison.Ordinal);
            Assert.Contains("RouteSeverity", pages, StringComparison.Ordinal);
            Assert.Contains("Uyarı İ", pages, StringComparison.Ordinal);

            (HttpStatusCode fgdb, _, byte[] gdb) = await ExportAsync(root, token, "fgdb");

            Assert.Equal(HttpStatusCode.OK, fgdb);

            StringBuilder tables = new();

            using (ZipArchive archive = new(new MemoryStream(gdb)))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    using MemoryStream copy = new();
                    using (Stream opened = entry.Open()) await opened.CopyToAsync(copy);
                    // A File Geodatabase keeps text as UTF-16, from an even or an odd byte; read it all three ways.
                    byte[] bytes = copy.ToArray();
                    tables.Append(Encoding.Unicode.GetString(bytes))
                        .Append(bytes.Length > 1 ? Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1) : string.Empty)
                        .Append(Encoding.UTF8.GetString(bytes));
                }
            }

            Assert.Contains("RouteSeverity", tables.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            await FieldsAsync("""{"overrides":[]}""");
        }
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

        // Esri JSON: a FeatureSet as `query?f=json` answers it, with no page flag, because a file has no next page.
        (HttpStatusCode esri, string? esriType, byte[] featureSet) = await ExportAsync(root, token, "esrijson");

        Assert.Equal(HttpStatusCode.OK, esri);
        Assert.Equal("application/json", esriType);

        using (JsonDocument parsed = JsonDocument.Parse(featureSet))
        {
            JsonElement set = parsed.RootElement;
            Assert.True(set.TryGetProperty("geometryType", out _), "The FeatureSet names no geometry type.");
            Assert.True(set.GetProperty("fields").GetArrayLength() > 0);
            Assert.True(set.GetProperty("features").GetArrayLength() > 0);
            Assert.True(set.GetProperty("features")[0].TryGetProperty("attributes", out _));
            Assert.False(set.TryGetProperty("exceededTransferLimit", out _), "A whole layer's file says there is a next page.");
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
