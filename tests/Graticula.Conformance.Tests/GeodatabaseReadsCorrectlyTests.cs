using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// A valid geodatabase is imported and served, field types and all.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-95](../../docs/architecture-debt.md) and [Q-138](../../docs/open-questions.md), owner
/// decision 2026-09-03.</b> Until today the only automated test of the geodatabase path drove it
/// with **four empty files** and asserted that GDAL refused them and that the refusal was
/// recorded. That is worth having — a dead worker or a job stuck at *queued* is the failure it
/// prevents — and it says nothing about whether a real archive reads correctly. The corpus that
/// would have said so is the owner's client data, which does not enter this repository, and a
/// public-domain File Geodatabase would put somebody else's bytes and a data licence into a
/// repository that is given away.
/// </para>
/// <para>
/// <b>So the archive is built at test time, by the reader's own GDAL.</b> `Graticula.Import.Reader`
/// answers a `fixture` operation that writes two layers — points and polygons, in EPSG:4326,
/// with a text, an integer, a real and a date field — and this zips that directory and puts it
/// through the import the console uses.
/// </para>
/// <para>
/// <b>What this does not test, said plainly because Q-138 said it first.</b> It reads what
/// **GDAL's writer** produced, not what Esri's did, and a fair part of the format risk lives in
/// that difference. What it does test is everything between the upload and the served feature:
/// the archive is recognised, the job runs, two layers become a service, the field types survive,
/// the geometry survives, and the features come back through the query face. A regression in any
/// of those is now found by the suite rather than by a person.
/// </para>
/// <para>
/// <b>It fails rather than skips when the reader is not built.</b> A test that quietly passes
/// because it could not find the thing it tests is how this row stayed open while looking
/// covered.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class GeodatabaseReadsCorrectlyTests : ArcGisClient
{
    private const string Service = "zz_gdb_fixture";

    [Fact]
    public async Task A_geodatabase_becomes_a_service_that_serves_what_was_in_it()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string reader = Reader();
        string work = Path.Combine(Path.GetTempPath(), "graticula-gdb-" + Guid.NewGuid().ToString("n"));

        Directory.CreateDirectory(work);

        // <b>Cleared before as well as after.</b> A run that died between publishing and
        // deleting leaves the service behind, and the next run's publish is refused with *there
        // is already a service called this* -- a failure about the previous run rather than
        // about the code.
        await DeleteAsync(
            $"{root}/admin/featureservices/{Service}?folder=hosted&drop=true", token!);

        try
        {
            string gdb = Path.Combine(work, "probe.gdb").Replace('\\', '/');

            string made = Ask(reader, $"{{\"op\":\"fixture\",\"path\":\"{gdb}\"}}");

            JsonElement fixture = JsonDocument.Parse(made).RootElement;

            Assert.Equal(2, fixture.GetProperty("layers").GetInt32());
            Assert.Equal(10, fixture.GetProperty("features").GetInt32());

            byte[] archive = Zip(gdb, "probe.gdb");

            Assert.True(
                archive.Length > 4096,
                $"The zipped geodatabase is {archive.Length} bytes, which is too small to be one.");

            (HttpStatusCode accepted, string opened) =
                await ImportAsync(root, token!, archive, "probe.gdb.zip");

            Assert.True(
                accepted == HttpStatusCode.Accepted,
                $"The import answered {(int)accepted}: {opened}");

            string job = JsonDocument.Parse(opened).RootElement.GetProperty("job").GetString()!;

            (string status, string failure, string detail) =
                await SettledAsync(root, token!, job);

            // <b>`done`, which is `JobStatus.Done` on the wire.</b> The first version of this
            // asked for `succeeded` and failed against a job that had worked — a test that
            // guesses at a vocabulary it could read.
            Assert.True(
                status == "done",
                $"The upload's inspection finished as '{status}'. {failure}");

            // <b>The inspection is half the read path and it is asserted here.</b> Uploading a
            // geodatabase inspects it; what the operator does next is choose which layers to
            // publish. This is where the reader's answer about the archive is visible, so what
            // it says about types and geometry is checked before anything is published — a
            // service that came out right could still have come out right for the wrong reason.
            Assert.Contains("\"places\"", detail, StringComparison.Ordinal);
            Assert.Contains("\"parcels\"", detail, StringComparison.Ordinal);
            Assert.Contains("wkbPoint", detail, StringComparison.Ordinal);
            Assert.Contains("\"srid\":4326", detail, StringComparison.Ordinal);

            // <b>Then the publish, which is the act the console performs.</b> Both layers named,
            // because ADR-038 turns each into a layer of one service and a one-layer fixture
            // would leave the path the owner's archives take unexercised.
            (HttpStatusCode published, string publishedWhy) = await PublishAsync(
                root,
                token!,
                // <b>One interpolated string, because `}}` in the second half was two braces.</b>
                // Concatenating an interpolated string with a plain one loses the escaping rule
                // at the seam, and the body went out with a trailing `}}` -- a 400 with an empty
                // message, which is the least useful refusal there is.
                $"{{\"archive\":\"{job}\",\"service\":\"{Service}\","
                + $"\"layers\":[\"places\",\"parcels\"]}}");

            Assert.True(
                published is HttpStatusCode.OK or HttpStatusCode.Created
                    or HttpStatusCode.Accepted,
                $"Publishing the inspected archive answered {(int)published}: {publishedWhy}");

            if (published == HttpStatusCode.Accepted)
            {
                string second = JsonDocument.Parse(publishedWhy).RootElement
                    .GetProperty("job").GetString()!;

                (string landed, string landedWhy, _) = await SettledAsync(root, token!, second);

                Assert.True(
                    landed == "done",
                    $"Publishing finished as '{landed}'. {landedWhy}");
            }

            (HttpStatusCode described, string document) =
                await GetAsync($"{root}/rest/services/hosted/{Service}/FeatureServer?f=json", token!);

            Assert.Equal(HttpStatusCode.OK, described);

            JsonElement layers = JsonDocument.Parse(document).RootElement.GetProperty("layers");

            Assert.True(
                layers.GetArrayLength() == 2,
                $"The service has {layers.GetArrayLength()} layer(s); the archive had two.");

            HashSet<string> named = [];

            foreach (JsonElement layer in layers.EnumerateArray())
            {
                named.Add(layer.GetProperty("name").GetString() ?? string.Empty);
            }

            Assert.Contains("places", named);
            Assert.Contains("parcels", named);

            // <b>The fields, because what a reader gets wrong is types rather than coordinates.</b>
            // A fixture with one string column would pass while the inference was broken.
            (HttpStatusCode layerRead, string layerDocument) = await GetAsync(
                $"{root}/rest/services/hosted/{Service}/FeatureServer/0?f=json", token!);

            Assert.Equal(HttpStatusCode.OK, layerRead);

            Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);

            foreach (JsonElement field in
                JsonDocument.Parse(layerDocument).RootElement.GetProperty("fields").EnumerateArray())
            {
                fields[field.GetProperty("name").GetString() ?? string.Empty] =
                    field.GetProperty("type").GetString() ?? string.Empty;
            }

            foreach (string wanted in new[] { "name", "count", "area", "seen" })
            {
                Assert.True(
                    fields.ContainsKey(wanted),
                    $"The served layer has no '{wanted}' field. It has: "
                    + string.Join(", ", fields.Keys));
            }

            Assert.Equal("esriFieldTypeString", fields["name"]);
            Assert.Equal("esriFieldTypeInteger", fields["count"]);
            Assert.Equal("esriFieldTypeDouble", fields["area"]);

            // <b>And the features, which is the half a document cannot claim.</b> A service can
            // describe a layer it cannot read.
            (HttpStatusCode queried, string answer) = await GetAsync(
                $"{root}/rest/services/hosted/{Service}/FeatureServer/0/query"
                + "?where=1%3D1&outFields=*&returnGeometry=true&f=json", token!);

            Assert.Equal(HttpStatusCode.OK, queried);

            JsonElement features = JsonDocument.Parse(answer).RootElement.GetProperty("features");

            Assert.True(
                features.GetArrayLength() == 5,
                $"The query returned {features.GetArrayLength()} features; the layer had five.");

            JsonElement first = features[0];

            Assert.True(
                first.TryGetProperty("geometry", out JsonElement geometry)
                && geometry.ValueKind is not JsonValueKind.Null,
                "A feature came back with no geometry, so the archive's shapes did not survive.");

            string text = first.GetProperty("attributes").GetProperty("name").GetString()
                ?? string.Empty;

            Assert.StartsWith("places ", text, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteAsync(
            $"{root}/admin/featureservices/{Service}?folder=hosted&drop=true", token!);

            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // A fixture left in the temporary directory is not worth failing a green run.
            }
        }
    }

    /// <summary>The reader executable, found from this test's own location.</summary>
    /// <remarks>
    /// <b>Walked up to the repository rather than configured.</b> The suite already knows where
    /// it is; asking for one more environment variable would be one more thing to set wrongly,
    /// and the failure when it is missing says what to build.
    /// </remarks>
    private static string Reader()
    {
        DirectoryInfo? at = new(AppContext.BaseDirectory);

        while (at is not null && !File.Exists(Path.Combine(at.FullName, "Graticula.sln")))
        {
            at = at.Parent;
        }

        Assert.False(at is null, "This test could not find the repository root from its own path.");

        string name = OperatingSystem.IsWindows()
            ? "Graticula.Import.Reader.exe"
            : "Graticula.Import.Reader";

        foreach (string configuration in new[] { "Debug", "Release" })
        {
            string candidate = Path.Combine(
                at!.FullName, "src", "Graticula.Import.Reader", "bin", configuration, "net9.0", name);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        Assert.Fail(
            "The geodatabase reader is not built, so this test has nothing to make a fixture "
            + "with. Build src/Graticula.Import.Reader and run again — it is the same binary "
            + "the server spawns to read an archive.");

        return string.Empty;
    }

    /// <summary>Asks the reader one question and returns its answer.</summary>
    private static string Ask(string reader, string request)
    {
        ProcessStartInfo start = new(reader)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException($"'{reader}' did not start.");

        process.StandardInput.WriteLine(request);
        process.StandardInput.Close();

        string answer = process.StandardOutput.ReadToEnd();
        string complaint = process.StandardError.ReadToEnd();

        process.WaitForExit(120_000);

        // <b>The reader writes GDAL's warnings to stdout too.</b> The answer is the last line
        // that parses as an object, which is what the server's own client does.
        string? last = answer
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .LastOrDefault(line => line.StartsWith('{'));

        Assert.False(
            last is null,
            $"The reader answered nothing usable.\nstdout: {answer}\nstderr: {complaint}");

        return last!;
    }

    /// <summary>Zips a directory, with every entry under one folder.</summary>
    private static byte[] Zip(string directory, string folder)
    {
        using MemoryStream buffer = new();

        using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string file in Directory.GetFiles(directory))
            {
                ZipArchiveEntry entry = zip.CreateEntry($"{folder}/{Path.GetFileName(file)}");

                using Stream into = entry.Open();
                using FileStream from = File.OpenRead(file);

                from.CopyTo(into);
            }
        }

        return buffer.ToArray();
    }

    private async Task<(HttpStatusCode Status, string Body)> ImportAsync(
        string root, string token, byte[] archive, string filename)
    {
        using MultipartFormDataContent form = new();
        using ByteArrayContent bytes = new(archive);

        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(bytes, "file", filename);
        form.Add(new StringContent(Service), "name");

        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}/admin/hosted/import")
        {
            Content = form,
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<(HttpStatusCode Status, string Body)> PublishAsync(
        string root, string token, string body)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}/admin/hosted/geodatabase")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<(string Status, string Failure, string Detail)> SettledAsync(
        string root, string token, string job)
    {
        string last = "never asked";

        for (int attempt = 0; attempt < 60; attempt++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, $"{root}/admin/jobs/{job}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage response = await Http.SendAsync(request);

            JsonElement found = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync()).RootElement;

            last = found.GetProperty("status").GetString() ?? "unreadable";

            if (last is not ("queued" or "running"))
            {
                return (
                    last,
                    found.TryGetProperty("failure", out JsonElement why)
                        ? why.GetString() ?? string.Empty
                        : string.Empty,
                    found.TryGetProperty("detail", out JsonElement said)
                        ? said.GetString() ?? string.Empty
                        : string.Empty);
            }

            await Task.Delay(1000);
        }

        return (last, "the job never settled", string.Empty);
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string url, string token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task DeleteAsync(string url, string token)
    {
        using HttpRequestMessage request = new(HttpMethod.Delete, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        _ = response.StatusCode;
    }
}
