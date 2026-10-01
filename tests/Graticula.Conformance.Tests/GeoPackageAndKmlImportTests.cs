using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-120: a GeoPackage — on its own or zipped — and KML or KMZ are imported the way a File Geodatabase is: their
/// layers are listed, and the one chosen is published.
/// </summary>
/// <remarks>
/// <b>The GeoPackage is written at test time by the reader's own GDAL</b>, as the geodatabase test makes its archive;
/// the KML is text. Everything published is deleted.
/// </remarks>
[Collection("catalogue walk")]
public sealed class GeoPackageAndKmlImportTests : ArcGisClient
{
    private const string Kml =
        "<?xml version=\"1.0\"?><kml xmlns=\"http://www.opengis.net/kml/2.2\"><Document>"
        + "<Placemark><name>A</name><Point><coordinates>32.85,39.93</coordinates></Point></Placemark>"
        + "<Placemark><name>B</name><Point><coordinates>29.0,41.0</coordinates></Point></Placemark>"
        + "</Document></kml>";

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        string root, string token, HttpMethod method, string path, HttpContent? content = null)
    {
        using HttpRequestMessage request = new(method, $"{root}{path}") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Uploads a file, waits for its layers, publishes the first, and answers how many features it has.</summary>
    private async Task<int> ImportAsync(string root, string token, byte[] bytes, string fileName, string service)
    {
        using MultipartFormDataContent form = new();
        form.Add(new ByteArrayContent(bytes), "file", fileName);
        form.Add(new StringContent(service), "name");

        (HttpStatusCode accepted, string opened) = await SendAsync(root, token, HttpMethod.Post, "/admin/hosted/import", form);
        Assert.True(accepted == HttpStatusCode.Accepted, $"Uploading {fileName} answered {(int)accepted}: {opened}");

        string job = JsonDocument.Parse(opened).RootElement.GetProperty("job").GetString()!;
        string layer = (await SettledAsync(root, token, job)).GetProperty("layers")[0].GetProperty("name").GetString()!;

        (HttpStatusCode published, string publishBody) = await SendAsync(root, token, HttpMethod.Post, "/admin/hosted/geodatabase",
            new StringContent(JsonSerializer.Serialize(new { archive = job, service, layers = new[] { layer } }), Encoding.UTF8, "application/json"));
        Assert.True(published is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Accepted,
            $"Publishing {fileName} answered {(int)published}: {publishBody}");

        if (JsonDocument.Parse(publishBody).RootElement.TryGetProperty("job", out JsonElement publishJob))
        {
            await SettledAsync(root, token, publishJob.GetString()!);
        }

        (_, string counted) = await SendAsync(root, token, HttpMethod.Get,
            $"/rest/services/hosted/{service}/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json");
        return JsonDocument.Parse(counted).RootElement.GetProperty("count").GetInt32();
    }

    private async Task<JsonElement> SettledAsync(string root, string token, string job)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            (_, string body) = await SendAsync(root, token, HttpMethod.Get, $"/admin/jobs/{job}");
            JsonElement found = JsonDocument.Parse(body).RootElement;
            string status = found.GetProperty("status").GetString()!;

            if (status is not ("queued" or "running" or "pending"))
            {
                Assert.True(status == "done", $"Job {job} finished as '{status}': {body}");
                return JsonDocument.Parse(found.GetProperty("detail").GetString()!).RootElement.Clone();
            }

            await Task.Delay(500);
        }

        Assert.Fail($"Job {job} never settled.");
        return default;
    }

    private static byte[] Zip(string name, byte[] content)
    {
        using MemoryStream bytes = new();
        using (ZipArchive zip = new(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            using Stream into = zip.CreateEntry(name).Open();
            into.Write(content);
        }

        return bytes.ToArray();
    }

    [Fact]
    public async Task A_GeoPackage_and_KML_are_imported_as_a_geodatabase_is()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string stem = Guid.NewGuid().ToString("N")[..8];
        string[] services = [$"zz_gpkg_{stem}", $"zz_gpkgzip_{stem}", $"zz_kmz_{stem}", $"zz_kml_{stem}"];

        string work = Path.Combine(Path.GetTempPath(), $"adr120-{stem}");
        Directory.CreateDirectory(work);

        try
        {
            string json = Path.Combine(work, "points.geojson");
            await File.WriteAllTextAsync(json,
                """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[32.85,39.93]},"properties":{"ad":"Ankara"}},{"type":"Feature","geometry":{"type":"Point","coordinates":[27.14,38.42]},"properties":{"ad":"Izmir"}},{"type":"Feature","geometry":{"type":"Point","coordinates":[29.06,40.18]},"properties":{"ad":"Bursa"}}]}""");
            string gpkg = Path.Combine(work, "cities.gpkg");
            string made = GeodatabaseReadsCorrectlyTests.Ask(GeodatabaseReadsCorrectlyTests.Reader(),
                JsonSerializer.Serialize(new { op = "export", @in = json, @out = gpkg, format = "gpkg", layer = "cities" }));
            Assert.True(File.Exists(gpkg), $"The reader made no GeoPackage: {made}");
            byte[] package = await File.ReadAllBytesAsync(gpkg);

            Assert.Equal(3, await ImportAsync(root, token!, package, "cities.gpkg", services[0]));
            Assert.Equal(3, await ImportAsync(root, token!, Zip("data/cities.gpkg", package), "cities.zip", services[1]));
            Assert.Equal(2, await ImportAsync(root, token!, Zip("doc.kml", Encoding.UTF8.GetBytes(Kml)), "places.kmz", services[2]));
            Assert.Equal(2, await ImportAsync(root, token!, Encoding.UTF8.GetBytes(Kml), "places.kml", services[3]));
        }
        finally
        {
            foreach (string service in services)
            {
                await SendAsync(root, token!, HttpMethod.Delete, $"/admin/featureservices/{service}?folder=hosted&drop=true");
            }

            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }
}
