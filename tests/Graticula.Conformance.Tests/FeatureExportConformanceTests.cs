using System;
using System.Collections.Generic;
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
/// Some of a service's layers exported as a file, as a job — ADR-106.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read by what each file is, not by the status alone</b>, as <see cref="LayerExportConformanceTests"/> reads the
/// synchronous route's: a GeoPackage is an SQLite database and says so in its first sixteen bytes, and its tables are in
/// the bytes the database stores its schema in; a zipped Shapefile holds a <c>.shp</c> for each layer; a workbook holds a
/// sheet for each. A 200 with an error page as its body passes none of them.
/// </para>
/// <para>
/// <b>Against <c>GRATICULA_TEST_MULTILAYER</c></b> (default <c>hosted/ci_EarlyAlert</c>), which has to have more than one
/// layer, because packaging several layers into one file is what the job is for. The suite's own account exports; every
/// test cancels what it started, since a caller has one export at a time and the next test is the same caller.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class FeatureExportConformanceTests : ArcGisClient
{
    private const string MultilayerVariable = "GRATICULA_TEST_MULTILAYER";

    private const string Reader = "zz_export_reader";

    private static readonly int[] NoSuchLayer = [9999];

    private static readonly string[] ExtractOnly = ["Extract"];

    private static (string Bare, string Folder) Service()
    {
        string named = Environment.GetEnvironmentVariable(MultilayerVariable) ?? "hosted/ci_EarlyAlert";
        string[] parts = named.Trim('/').Split('/');

        return (parts[^1], parts.Length > 1 ? string.Join('/', parts[..^1]) : string.Empty);
    }

    /// <summary>The route, with its folder, and whatever else the query carries.</summary>
    private static string Route(string tail = "", string more = "")
    {
        (string bare, string folder) = Service();

        return $"/admin/services/{Uri.EscapeDataString(bare)}/data-exports{tail}?folder={Uri.EscapeDataString(folder)}{more}";
    }

    private async Task<(int Status, string Body, HttpResponseMessage Response)> SendAsync(
        HttpMethod method, string path, string? token, string? json = null, string? range = null)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(method, new Uri(root + path));

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (range is not null)
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        HttpResponseMessage response = await Http.SendAsync(request);

        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(), response);
    }

    private async Task<(int Status, byte[] Bytes, HttpResponseMessage Response)> FetchAsync(
        string path, string? token, string? range = null)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + path));

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (range is not null)
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        HttpResponseMessage response = await Http.SendAsync(request);

        return ((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync(), response);
    }

    private async Task<string> AdminTokenAsync()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        return token!;
    }

    /// <summary>The service's layers as the route lists them: their ids and names.</summary>
    private async Task<List<(int Id, string Name)>> LayersAsync(string token)
    {
        (int status, string body, _) = await SendAsync(HttpMethod.Get, Route(), token);

        Assert.True(status == 200, $"Reading the export list answered {status}: {body}");

        List<(int, string)> layers = [.. JsonDocument.Parse(body).RootElement.GetProperty("layers").EnumerateArray()
            .Select(l => (l.GetProperty("id").GetInt32(), l.GetProperty("name").GetString()!))];

        Assert.True(
            layers.Count > 1,
            $"{MultilayerVariable} names a service with {layers.Count} layer(s). The export job packages several layers into "
            + "one file, and a one-layer service would prove none of it.");

        return layers;
    }

    /// <summary>Cancels whatever the caller has waiting or running, so the next start is not a 409.</summary>
    private async Task ClearAsync(string token)
    {
        (_, string body, _) = await SendAsync(HttpMethod.Get, Route(), token);

        foreach (JsonElement export in JsonDocument.Parse(body).RootElement.GetProperty("exports").EnumerateArray())
        {
            if (export.GetProperty("status").GetString() is "queued" or "running")
            {
                await SendAsync(HttpMethod.Delete, export.GetProperty("watch").GetString()!, token);
            }
        }
    }

    private async Task<(int Status, JsonElement Body)> StartAsync(string? token, object request, string more = "")
    {
        (int status, string body, _) = await SendAsync(HttpMethod.Post, Route(more: more), token, JsonSerializer.Serialize(request));

        return (status, body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone());
    }

    private async Task<JsonElement> WaitAsync(string watch, string token, TimeSpan patience)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + patience;

        while (true)
        {
            (int status, string body, _) = await SendAsync(HttpMethod.Get, watch, token);
            Assert.True(status == 200, $"Watching {watch} answered {status}: {body}");

            JsonElement export = JsonDocument.Parse(body).RootElement.Clone();

            if (export.GetProperty("status").GetString() is "done" or "failed" or "cancelled")
            {
                return export;
            }

            Assert.True(DateTimeOffset.UtcNow < until, $"The export was still running after {patience.TotalMinutes} minutes: {export}");
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
    }

    /// <summary>Starts an export, waits for it, and answers what the caller downloads.</summary>
    private async Task<(JsonElement Export, byte[] File, HttpResponseMessage Response)> ExportAsync(
        string token, string format, IReadOnlyList<int>? layers = null)
    {
        (int status, JsonElement started) = await StartAsync(token, new { format, layers });

        Assert.True(status == 202, $"Starting a {format} export answered {status}: {started}");
        Assert.Equal("queued", started.GetProperty("status").GetString());

        JsonElement done = await WaitAsync(started.GetProperty("watch").GetString()!, token, TimeSpan.FromMinutes(5));

        Assert.True(done.GetProperty("status").GetString() == "done", $"The {format} export: {done}");

        (int fetched, byte[] file, HttpResponseMessage response) = await FetchAsync(done.GetProperty("download").GetString()!, token);

        Assert.True(fetched == 200, $"Downloading the {format} export answered {fetched}.");
        Assert.Equal(file.Length, response.Content.Headers.ContentLength);
        Assert.Equal(file.Length, done.GetProperty("bytes").GetInt64());

        return (done, file, response);
    }

    /// <summary>
    /// A field's coded-value domain leaves in a GeoPackage as a domain — ADR-106 conditions 1 and 2.
    /// </summary>
    /// <remarks>
    /// <b>On whatever this suite runs on</b>, which is the point: the domain is made through GDAL's C function, looked up in
    /// the native library by its file name, and that name differs between Windows and Linux. A GeoPackage keeps domains
    /// in <c>gpkg_data_column_constraints</c>, a table GDAL makes only when it writes one, and the codes' descriptions are
    /// in its pages; so both are looked for in the file's bytes.
    /// </remarks>
    [Fact]
    public async Task A_fields_domain_leaves_in_a_GeoPackage_as_a_domain()
    {
        string token = await AdminTokenAsync();
        const string Sites = "ci_EarlyAlert_sites";
        string fields = $"/admin/layers/{Sites}/fields";

        await ClearAsync(token);

        (int set, string said, _) = await SendAsync(HttpMethod.Put, fields, token, """
            {"overrides":[{"column":"severity","domain":{"type":"codedValue","name":"ExportSeverity",
             "codedValues":[{"code":0,"name":"Calm ğ"},{"code":1,"name":"Watch ş"},{"code":2,"name":"Warning İ"},{"code":3,"name":"Alert ı"}]}}]}
            """);
        Assert.True(set == 200, $"Setting the domain answered {set}: {said}");

        try
        {
            (JsonElement export, byte[] file, _) = await ExportAsync(token, "gpkg");

            try
            {
                string pages = Encoding.UTF8.GetString(file);
                Assert.Contains("gpkg_data_column_constraints", pages, StringComparison.Ordinal);
                Assert.Contains("ExportSeverity", pages, StringComparison.Ordinal);
                Assert.Contains("Warning İ", pages, StringComparison.Ordinal);
            }
            finally
            {
                await SendAsync(HttpMethod.Delete, export.GetProperty("watch").GetString()!, token);
            }
        }
        finally
        {
            await SendAsync(HttpMethod.Put, fields, token, """{"overrides":[]}""");
        }
    }

    [Fact]
    public async Task A_GeoPackage_of_every_layer_is_one_file_with_a_table_for_each()
    {
        string token = await AdminTokenAsync();
        List<(int Id, string Name)> layers = await LayersAsync(token);

        await ClearAsync(token);

        (JsonElement export, byte[] file, HttpResponseMessage response) = await ExportAsync(token, "gpkg");

        try
        {
            Assert.Equal("application/geopackage+sqlite3", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains(".gpkg", response.Content.Headers.ContentDisposition?.ToString() ?? string.Empty, StringComparison.Ordinal);
            Assert.True(
                response.Headers.CacheControl is { NoStore: true, Private: true },
                $"The download's Cache-Control is '{response.Headers.CacheControl}'.");
            Assert.StartsWith("SQLite format 3", Encoding.ASCII.GetString(file, 0, 15), StringComparison.Ordinal);

            // <b>One layer for each layer chosen</b>, as the job reports it and as the file holds them: the tables' names
            // are in the pages SQLite keeps its schema in.
            Assert.Equal(layers.Count, export.GetProperty("layerCount").GetInt32());
            Assert.Equal(layers.Select(l => l.Id), export.GetProperty("layers").EnumerateArray().Select(l => l.GetProperty("id").GetInt32()));
            Assert.Equal(layers.Select(l => l.Name), export.GetProperty("layers").EnumerateArray().Select(l => l.GetProperty("name").GetString()));
            Assert.True(export.GetProperty("rowsWritten").GetInt64() > 0, $"The export wrote no rows: {export}");
            Assert.Equal(export.GetProperty("rows").GetInt64(), export.GetProperty("rowsWritten").GetInt64());

            string schema = Encoding.Latin1.GetString(file);

            Assert.Contains("gpkg_contents", schema, StringComparison.Ordinal);
            Assert.All(layers, layer => Assert.True(
                schema.Contains($"\"{layer.Name}\"", StringComparison.Ordinal) || schema.Contains(layer.Name, StringComparison.Ordinal),
                $"The GeoPackage has no table for layer '{layer.Name}'."));

            // <b>A range is a range</b>, with its length — what a client resuming a download asks for.
            (int partial, byte[] first, _) = await FetchAsync(export.GetProperty("download").GetString()!, token, "bytes=0-99");

            Assert.Equal(206, partial);
            Assert.Equal(file.AsSpan(0, 100).ToArray(), first);

            // The caller's own list carries it, with the address to fetch it from.
            (_, string list, _) = await SendAsync(HttpMethod.Get, Route(), token);

            Assert.Contains(
                JsonDocument.Parse(list).RootElement.GetProperty("exports").EnumerateArray(),
                e => e.GetProperty("id").GetGuid() == export.GetProperty("id").GetGuid());
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, export.GetProperty("watch").GetString()!, token);
        }
    }

    [Fact]
    public async Task A_shapefile_export_of_two_layers_is_a_zip_holding_both_layers_files()
    {
        string token = await AdminTokenAsync();
        List<(int Id, string Name)> layers = await LayersAsync(token);
        List<(int Id, string Name)> two = layers.Take(2).ToList();

        await ClearAsync(token);

        (JsonElement export, byte[] file, HttpResponseMessage response) = await ExportAsync(token, "shapefile", [.. two.Select(l => l.Id)]);

        try
        {
            Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(2, export.GetProperty("layerCount").GetInt32());

            using ZipArchive zip = new(new MemoryStream(file));

            foreach ((_, string name) in two)
            {
                foreach (string extension in (string[])[".shp", ".dbf", ".prj"])
                {
                    Assert.True(
                        zip.Entries.Any(e => string.Equals(e.Name, name + extension, StringComparison.OrdinalIgnoreCase)),
                        $"The zip holds no {name}{extension}: {string.Join(", ", zip.Entries.Select(e => e.FullName))}");
                }
            }

            Assert.Equal(2, zip.Entries.Count(e => e.Name.EndsWith(".shp", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, export.GetProperty("watch").GetString()!, token);
        }
    }

    [Fact]
    public async Task Every_other_format_leaves_as_what_it_is_several_layers_in_one_file_or_a_file_for_each_in_a_zip()
    {
        string token = await AdminTokenAsync();
        List<(int Id, string Name)> layers = await LayersAsync(token);

        await ClearAsync(token);

        // Excel: one workbook, a sheet for each layer.
        (JsonElement book, byte[] xlsx, _) = await ExportAsync(token, "xlsx");
        using (ZipArchive zip = new(new MemoryStream(xlsx)))
        {
            Assert.Contains(zip.Entries, e => e.FullName == "[Content_Types].xml");
            Assert.Equal(layers.Count, zip.Entries.Count(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)));
        }

        Assert.EndsWith(".xlsx", book.GetProperty("fileName").GetString(), StringComparison.Ordinal);

        // File Geodatabase: a zipped .gdb folder, a table for each layer, the folder itself inside.
        (_, byte[] gdb, HttpResponseMessage gdbResponse) = await ExportAsync(token, "fgdb");
        Assert.Equal("application/zip", gdbResponse.Content.Headers.ContentType?.MediaType);
        using (ZipArchive zip = new(new MemoryStream(gdb)))
        {
            Assert.All(zip.Entries, e => Assert.Contains(".gdb/", e.FullName, StringComparison.Ordinal));
            Assert.True(
                zip.Entries.Count(e => e.Name.EndsWith(".gdbtable", StringComparison.Ordinal)) >= layers.Count,
                $"The geodatabase holds fewer tables than layers: {string.Join(", ", zip.Entries.Select(e => e.FullName))}");
        }

        // KML: one file, a document for each layer.
        (_, byte[] kml, HttpResponseMessage kmlResponse) = await ExportAsync(token, "kml");
        Assert.Equal("application/vnd.google-earth.kml+xml", kmlResponse.Content.Headers.ContentType?.MediaType);
        string placemarks = Encoding.UTF8.GetString(kml);
        Assert.Contains("<kml", placemarks, StringComparison.Ordinal);
        Assert.All(layers, layer => Assert.Contains(layer.Name, placemarks, StringComparison.Ordinal));

        // CSV, GeoJSON and Esri JSON: a file for each layer, and several of them are one zip.
        foreach ((string format, string extension) in new[] { ("csv", ".csv"), ("geojson", ".geojson"), ("esrijson", ".json") })
        {
            (_, byte[] many, HttpResponseMessage manyResponse) = await ExportAsync(token, format);
            Assert.Equal("application/zip", manyResponse.Content.Headers.ContentType?.MediaType);

            using ZipArchive zip = new(new MemoryStream(many));
            Assert.Equal(layers.Count, zip.Entries.Count(e => e.Name.EndsWith(extension, StringComparison.Ordinal)));

            // ... and one layer alone is the bare file.
            (_, byte[] one, HttpResponseMessage oneResponse) = await ExportAsync(token, format, [layers[0].Id]);
            Assert.NotEqual("application/zip", oneResponse.Content.Headers.ContentType?.MediaType);

            if (format == "csv")
            {
                Assert.Equal([0xEF, 0xBB, 0xBF], one.Take(3));
            }
            else
            {
                using JsonDocument parsed = JsonDocument.Parse(one);
                Assert.True(
                    parsed.RootElement.TryGetProperty(format == "geojson" ? "type" : "geometryType", out _),
                    $"The {format} file is not the document it should be.");
            }
        }
    }

    [Fact]
    public async Task A_cookie_alone_cannot_start_an_export_and_nobody_anonymous_can()
    {
        string root = await RequireServerAsync();
        string token = await AdminTokenAsync();

        await ClearAsync(token);

        CookieContainer jar = new();

        using HttpClient browser = new(new HttpClientHandler
        {
            CookieContainer = jar,
            UseCookies = true,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });

        using (FormUrlEncodedContent form = new(new List<KeyValuePair<string, string>>
               {
                   new("name", Environment.GetEnvironmentVariable(UserVariable) ?? "root"),
                   new("password", Environment.GetEnvironmentVariable(PasswordVariable) ?? string.Empty),
                   new("return", "/rest/services"),
               }))
        using (HttpResponseMessage _ = await browser.PostAsync(new Uri(root + "/rest/auth/login"), form))
        {
        }

        Assert.NotNull(jar.GetCookies(new Uri(root))["gis-session"]);

        // <b>The cookie signs reads only</b>, so a POST that carries nothing else is nobody's, whatever the browser says
        // about where it came from — and one the browser says is from another site is refused before it is asked anything.
        foreach (string? site in new[] { null, "cross-site", "same-site", "same-origin" })
        {
            using HttpRequestMessage forged = new(HttpMethod.Post, new Uri(root + Route()))
            {
                Content = new StringContent("""{"format":"gpkg"}""", Encoding.UTF8, "application/json"),
            };

            if (site is not null)
            {
                forged.Headers.Add("Sec-Fetch-Site", site);
            }

            using HttpResponseMessage refused = await browser.SendAsync(forged);
            string said = await refused.Content.ReadAsStringAsync();

            Assert.True(
                refused.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                $"A cookie-only export POST with Sec-Fetch-Site {site ?? "absent"} answered {(int)refused.StatusCode}: {said}");
        }

        // Nobody anonymous, ever.
        (int anonymous, string body, _) = await SendAsync(HttpMethod.Post, Route(), null, """{"format":"gpkg"}""");

        Assert.True(anonymous == 401, $"An anonymous export answered {anonymous}: {body}");

        foreach (string path in new[] { Route(), Route("/" + Guid.NewGuid()), Route("/" + Guid.NewGuid() + "/download") })
        {
            (int read, _, _) = await SendAsync(HttpMethod.Get, path, null);

            Assert.True(read == 401, $"An anonymous read of {path} answered {read}.");
        }

        (int deleted, _, _) = await SendAsync(HttpMethod.Delete, Route("/" + Guid.NewGuid()), null);

        Assert.Equal(401, deleted);

        // A bearer header signs it, as it does for every other client.
        (int signed, JsonElement started) = await StartAsync(token, new { format = "csv" });

        Assert.True(signed == 202, $"A token-signed export answered {signed}: {started}");
        await SendAsync(HttpMethod.Delete, started.GetProperty("watch").GetString()!, token);
    }

    [Fact]
    public async Task A_second_export_by_the_same_caller_while_one_is_waiting_is_a_409_naming_the_first()
    {
        string token = await AdminTokenAsync();

        await LayersAsync(token);
        await ClearAsync(token);

        // <b>Asked together</b>, because the check is made under the same lock as the insert: however the requests land,
        // one is started and the others are told.
        (int Status, JsonElement Body)[] answers = await Task.WhenAll(
            Enumerable.Range(0, 3).Select(_ => StartAsync(token, new { format = "gpkg" })));

        try
        {
            Assert.Single(answers, a => a.Status == 202);
            Assert.Equal(2, answers.Count(a => a.Status == 409));

            string first = answers.Single(a => a.Status == 202).Body.GetProperty("id").GetString()!;

            foreach ((int _, JsonElement body) in answers.Where(a => a.Status == 409))
            {
                Assert.Equal("exportInProgress", body.GetProperty("error").GetProperty("details")[0].GetString());
                Assert.Equal(first, body.GetProperty("detail").GetProperty("job").GetString());
            }
        }
        finally
        {
            await ClearAsync(token);
        }

        // Once it is cancelled the caller may ask again.
        (int again, JsonElement next) = await StartAsync(token, new { format = "gpkg" });

        Assert.True(again == 202, $"Asking again after cancelling answered {again}: {next}");
        await ClearAsync(token);
    }

    [Fact]
    public async Task Deleting_an_export_removes_its_file_and_the_download_is_then_a_404()
    {
        string token = await AdminTokenAsync();

        await LayersAsync(token);
        await ClearAsync(token);

        (JsonElement export, _, _) = await ExportAsync(token, "gpkg");

        string download = export.GetProperty("download").GetString()!;

        // A guessed file name is not the file.
        string guessed = download.Contains("file=", StringComparison.Ordinal)
            ? download[..(download.IndexOf("file=", StringComparison.Ordinal) + 5)] + new string('0', 32)
            : download + "&file=" + new string('0', 32);

        (int wrong, _, _) = await FetchAsync(guessed, token);

        Assert.Equal(404, wrong);

        (int deleted, string said, _) = await SendAsync(HttpMethod.Delete, export.GetProperty("watch").GetString()!, token);

        Assert.True(deleted == 200, $"Deleting the export answered {deleted}: {said}");

        (int gone, _, _) = await FetchAsync(download, token);

        Assert.Equal(404, gone);

        // It is still an export the caller can see, and it says it was removed.
        (_, string watched, _) = await SendAsync(HttpMethod.Get, export.GetProperty("watch").GetString()!, token);

        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(watched).RootElement.GetProperty("download").ValueKind);
    }

    [Fact]
    public async Task A_dry_run_counts_and_estimates_and_starts_nothing_and_a_wrong_request_is_a_400()
    {
        string token = await AdminTokenAsync();
        List<(int Id, string Name)> layers = await LayersAsync(token);

        await ClearAsync(token);

        (_, string before, _) = await SendAsync(HttpMethod.Get, Route(), token);
        int listed = JsonDocument.Parse(before).RootElement.GetProperty("exports").GetArrayLength();

        (int status, JsonElement dry) = await StartAsync(token, new { format = "Excel" }, "&dryRun=true");

        Assert.True(status == 200, $"A dry run answered {status}: {dry}");
        Assert.Equal("xlsx", dry.GetProperty("format").GetString());
        Assert.Equal(layers.Count, dry.GetProperty("layers").GetArrayLength());
        Assert.True(dry.GetProperty("rows").GetInt64() > 0);
        Assert.True(dry.GetProperty("estimatedBytes").GetInt64() > 0);
        Assert.True(dry.GetProperty("fits").GetBoolean());
        Assert.True(dry.GetProperty("oneFile").GetBoolean());
        Assert.NotEmpty(dry.GetProperty("losses").EnumerateArray());

        (_, string after, _) = await SendAsync(HttpMethod.Get, Route(), token);

        Assert.Equal(listed, JsonDocument.Parse(after).RootElement.GetProperty("exports").GetArrayLength());

        (int unknown, _) = await StartAsync(token, new { format = "dxf" });
        Assert.Equal(400, unknown);

        (int missing, _) = await StartAsync(token, new { });
        Assert.Equal(400, missing);

        (int nolayer, JsonElement refused) = await StartAsync(token, new { format = "gpkg", layers = NoSuchLayer });
        Assert.True(nolayer == 400, $"A layer that is not there answered {nolayer}: {refused}");

        (int nothing, _, _) = await SendAsync(HttpMethod.Get, Route("/" + Guid.NewGuid()), token);
        Assert.Equal(404, nothing);
    }

    /// <summary>A service document's capabilities, and whether it says Editing and hasStaticData, for a caller.</summary>
    private async Task<(string Capabilities, bool Editing, bool StaticData)> CapabilitiesAsync(string document, string token)
    {
        (int status, string body, _) = await SendAsync(HttpMethod.Get, document, token);
        Assert.True(status == 200, $"{document} answered {status}: {body}");
        JsonElement service = JsonDocument.Parse(body).RootElement;
        string capabilities = service.GetProperty("capabilities").GetString() ?? string.Empty;
        bool staticData = service.TryGetProperty("hasStaticData", out JsonElement s) && s.ValueKind == JsonValueKind.True;
        return (capabilities, capabilities.Split(',').Contains("Editing", StringComparer.Ordinal), staticData);
    }

    [Fact]
    public async Task A_reader_exports_only_where_the_owner_offered_Extract_sees_only_their_own_and_loses_the_download_when_it_is_withdrawn()
    {
        string root = await RequireServerAsync();
        string admin = await AdminTokenAsync();
        List<(int Id, string Name)> layers = await LayersAsync(admin);
        (string bare, string folder) = Service();
        string editing = $"/admin/services/{Uri.EscapeDataString(bare)}/editing?folder={Uri.EscapeDataString(folder)}";

        await ClearAsync(admin);

        try
        {
            // <b>Made here and removed at the end, with its export</b> — which is itself asserted below. A run that
            // stopped halfway leaves it, so an existing probe is given a new password rather than refused.
            (int created, string made, _) = await SendAsync(
                HttpMethod.Post, "/admin/members", admin,
                JsonSerializer.Serialize(new { name = Reader, role = "user", userType = "creator" }));

            if (created == 409)
            {
                (created, made, _) = await SendAsync(HttpMethod.Put, $"/admin/members/{Reader}/password", admin);
            }

            Assert.True(created is 200 or 201, $"Providing the probe member answered {created}: {made}");

            string generated = JsonDocument.Parse(made).RootElement.GetProperty("password").GetString()!;
            string wanted = "Probe-" + Guid.NewGuid().ToString("N")[..12] + "!";
            string? first = await SignInAsync(root, generated);

            Assert.NotNull(first);

            (int changed, string changeSaid, _) = await SendAsync(
                HttpMethod.Post, "/rest/auth/password", first,
                JsonSerializer.Serialize(new { currentPassword = generated, newPassword = wanted }));

            Assert.True(changed == 200, $"Changing the probe's password answered {changed}: {changeSaid}");

            string reader = (await SignInAsync(root, wanted))!;

            Assert.NotNull(reader);

            // <b>Nothing chosen, nothing offered</b> (ADR-106 §5.5): the reader may look at the service and may not take it.
            (int refused, string sentence, _) = await SendAsync(HttpMethod.Post, Route(), reader, """{"format":"gpkg"}""");

            Assert.True(
                refused == 403 && sentence.Contains("Extract", StringComparison.Ordinal),
                $"A reader's export before Extract was offered answered {refused}: {sentence}");

            // <b>Advertised as the service's setting, not the caller's</b> (§5.5, amended 2026-09-30): before the offer
            // nobody is told Extract, the administrator included; Editing and hasStaticData come from the edits alone.
            string document = $"/rest/services/{Uri.EscapeDataString(folder)}/{Uri.EscapeDataString(bare)}/FeatureServer?f=json";
            (string readerCaps, bool readerEditing, bool readerStatic) = await CapabilitiesAsync(document, reader);
            (string adminCaps, _, _) = await CapabilitiesAsync(document, admin);
            Assert.DoesNotContain("Extract", readerCaps, StringComparison.Ordinal);
            Assert.DoesNotContain("Extract", adminCaps, StringComparison.Ordinal);

            // <b>The owner and administrators export whatever is offered</b>: the administrator, before any offer.
            (int unoffered, JsonElement adminFirst) = await StartAsync(admin, new { format = "csv", layers = new[] { layers[0].Id } });
            Assert.True(unoffered == 202, $"The administrator's export where Extract is not offered answered {unoffered}: {adminFirst}");
            JsonElement adminFirstDone = await WaitAsync(adminFirst.GetProperty("watch").GetString()!, admin, TimeSpan.FromMinutes(5));
            Assert.Equal("done", adminFirstDone.GetProperty("status").GetString());
            await SendAsync(HttpMethod.Delete, adminFirstDone.GetProperty("watch").GetString()!, admin);

            (int offered, string offeredSaid, _) = await SendAsync(
                HttpMethod.Put, editing, admin, JsonSerializer.Serialize(new { operations = ExtractOnly }));

            Assert.True(offered == 200, $"Offering Extract answered {offered}: {offeredSaid}");

            // Offered: every signed-in caller who may read it is told, and nothing else in the document moves.
            (string readerOffered, bool editingOffered, bool staticOffered) = await CapabilitiesAsync(document, reader);
            (string adminOffered, _, _) = await CapabilitiesAsync(document, admin);
            Assert.Contains("Extract", readerOffered, StringComparison.Ordinal);
            Assert.Contains("Extract", adminOffered, StringComparison.Ordinal);
            Assert.Equal(readerEditing, editingOffered);
            Assert.Equal(readerStatic, staticOffered);

            // The administrator has an export of their own, which is not the reader's to see.
            (int adminStatus, JsonElement adminStarted) = await StartAsync(admin, new { format = "csv", layers = new[] { layers[0].Id } });

            Assert.True(adminStatus == 202, $"The administrator's export answered {adminStatus}: {adminStarted}");

            JsonElement adminDone = await WaitAsync(adminStarted.GetProperty("watch").GetString()!, admin, TimeSpan.FromMinutes(5));
            string adminWatch = adminDone.GetProperty("watch").GetString()!;
            string adminDownload = adminDone.GetProperty("download").GetString()!;

            (int startedByReader, JsonElement own) = await StartAsync(reader, new { format = "gpkg" });

            Assert.True(startedByReader == 202, $"The reader's export where Extract is offered answered {startedByReader}: {own}");

            JsonElement done = await WaitAsync(own.GetProperty("watch").GetString()!, reader, TimeSpan.FromMinutes(5));

            Assert.Equal("done", done.GetProperty("status").GetString());

            string download = done.GetProperty("download").GetString()!;

            (int fetched, byte[] file, _) = await FetchAsync(download, reader);

            Assert.Equal(200, fetched);
            Assert.StartsWith("SQLite format 3", Encoding.ASCII.GetString(file, 0, 15), StringComparison.Ordinal);

            // <b>Only the one who started it, or an administrator</b>: the same 404 as an export that never was.
            (int theirs, _, _) = await SendAsync(HttpMethod.Get, adminWatch, reader);
            Assert.Equal(404, theirs);
            (int theirsDownload, _, _) = await FetchAsync(adminDownload, reader);
            Assert.Equal(404, theirsDownload);
            (int theirsDelete, _, _) = await SendAsync(HttpMethod.Delete, adminWatch, reader);
            Assert.Equal(404, theirsDelete);

            (_, string readersList, _) = await SendAsync(HttpMethod.Get, Route(), reader);
            JsonElement seen = JsonDocument.Parse(readersList).RootElement;
            Assert.All(seen.GetProperty("exports").EnumerateArray(), e => Assert.Equal(done.GetProperty("owner").GetGuid(), e.GetProperty("owner").GetGuid()));

            // The administrator sees the reader's too, and may take it.
            (_, string adminsList, _) = await SendAsync(HttpMethod.Get, Route(), admin);
            Assert.Contains(
                JsonDocument.Parse(adminsList).RootElement.GetProperty("exports").EnumerateArray(),
                e => e.GetProperty("id").GetGuid() == done.GetProperty("id").GetGuid());
            (int adminFetch, _, _) = await FetchAsync(download, admin);
            Assert.Equal(200, adminFetch);

            // <b>Withdrawn, and the reader's next download is refused</b> — asked again every time.
            await SendAsync(HttpMethod.Put, editing, admin, JsonSerializer.Serialize(new { operations = (string[]?)null }));

            (int withdrawn, string why, _) = await FetchTextAsync(download, reader);
            Assert.True(withdrawn == 403, $"A download after Extract was withdrawn answered {withdrawn}: {why}");

            (int again, _, _) = await FetchAsync(download, admin);
            Assert.Equal(200, again);

            // <b>A member who exported can still be removed</b>, their export with them: `job.owner_principal_id` has
            // no cascade, and until 2026-09-30 this answered 409 on `job_owner_principal_id_fkey`, so every reader who
            // ever exported became undeletable. The reader's export is left in place on purpose.
            (int removed, string said, _) = await SendAsync(HttpMethod.Delete, $"/admin/members/{Reader}", admin);
            Assert.True(removed is 200 or 204, $"Removing a member who exported answered {removed}: {said}");

            (int gone, _, _) = await FetchAsync(done.GetProperty("watch").GetString()!, admin);
            Assert.Equal(404, gone);

            await SendAsync(HttpMethod.Delete, adminWatch, admin);
        }
        finally
        {
            await SendAsync(HttpMethod.Put, editing, admin, JsonSerializer.Serialize(new { operations = (string[]?)null }));
            await ClearAsync(admin);
        }
    }

    private async Task<(int Status, string Body, HttpResponseMessage Response)> FetchTextAsync(string path, string token)
    {
        (int status, byte[] bytes, HttpResponseMessage response) = await FetchAsync(path, token);

        return (status, Encoding.UTF8.GetString(bytes), response);
    }

    private async Task<string?> SignInAsync(string root, string password)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}/rest/auth/login")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { name = Reader, password }), Encoding.UTF8, "application/json"),
        };

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"Signing in as {Reader} answered {(int)response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.TryGetProperty("token", out JsonElement token) ? token.GetString() : null;
    }
}
