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
/// An archive this server cannot import is refused by name, not by what it is not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written 2026-08-18, after the owner asked what our structure is for GDB and shapefiles.</b> The
/// investigation found the import path correct and its refusal useless: a File Geodatabase is a ZIP,
/// so it entered the shapefile path and came back with *"the archive entry 'roads.gdb/…' is inside a
/// folder — zip the shapefile's files directly rather than the folder holding them."* Every word true,
/// and the advice cannot be followed: a `.gdb` **is** a folder, and flattening it would not make it a
/// shapefile. The person receiving that sentence is exactly the person this product is for —
/// [ADR-024](../../docs/adr/ADR-024-shapefile-import.md) §1: *"the people this product is for have
/// shapefiles"*, and their other data is in geodatabases.
/// </para>
/// <para>
/// <b>Recognition runs before the attempt, and putting it after failed twice.</b> A geodatabase never
/// reaches bundle assembly, because the archive reader refuses folders first; and the second
/// <c>OpenReadStream()</c> came back unusable, so the recogniser silently answered *nothing
/// recognised* for every archive. Both were found by measuring the refusal rather than reading the
/// code, which is why this suite asserts the sentence rather than the status code.
/// </para>
/// </remarks>
public sealed class ArchiveFormatRefusalTests : ArcGisClient
{
    /// <summary>
    /// A File Geodatabase now opens a job, and the job runs the reader and records what happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test asserted a 400 until 2026-08-19, and the change of answer is the feature.</b> The
    /// endpoint used to refuse a geodatabase with a sentence naming Q-108. It now writes the archive to
    /// the import scratch directory, opens a <c>geodatabase.inspect</c> job and answers <c>202</c> with
    /// the address to watch — [ADR-034](../../docs/adr/ADR-034-server-and-studio.md) §5j and
    /// [ADR-037](../../docs/adr/ADR-037-job-workers-come-in-two-kinds.md).
    /// </para>
    /// <para>
    /// <b>Four empty files in a `roads.gdb/` folder, and that is not a shortcut.</b> A real geodatabase
    /// in the test corpus would be somebody's data — the owner's three are real client archives and
    /// stay out of this repository — so the archive is built here and carries no Esri bytes. What it
    /// buys is the whole pipeline: the recogniser reads entry names, the endpoint keeps the file, the
    /// inspector claims the job, spawns the reader, GDAL is handed a folder that is not a geodatabase,
    /// and the refusal comes back as a recorded failure rather than as a dead worker or a job stuck at
    /// queued for ever.
    /// </para>
    /// <para>
    /// <b>So the assertion is *it failed, with a reason*, and that is the interesting outcome.</b> A
    /// job that succeeded here would mean GDAL had read four empty files as a geodatabase. Reading a
    /// real one is measured instead — `docs/research/file-geodatabase-readers.md` §8f, three archives,
    /// 12, 55 and 8 layers — because that is a fact about somebody's data and cannot live in a test.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_file_geodatabase_opens_a_job_and_the_job_says_what_happened()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        byte[] archive = Zip(
            ("roads.gdb/a00000001.gdbtable", 64),
            ("roads.gdb/a00000001.gdbtablx", 64),
            ("roads.gdb/a00000004.gdbindexes", 64),
            ("roads.gdb/gdb", 64));

        (HttpStatusCode status, string message) = await ImportAsync(
            root, token!, archive, "probe.gdb.zip", "zz_gdb_pipeline");

        // <b>A deployment without the reader still refuses, and that is a different test.</b> This one
        // is about the pipeline, so a 400 here means the reader was not shipped beside the server —
        // which the message says, and which is worth failing loudly rather than skipping quietly.
        Assert.True(
            status == HttpStatusCode.Accepted,
            $"Expected 202 with a job. Got {(int)status}: {message}");

        JsonElement opened = JsonDocument.Parse(message).RootElement;

        string job = opened.GetProperty("job").GetString()
            ?? throw new InvalidOperationException("The 202 carried no job id.");

        // Where to watch, because a 202 that does not say is a 202 nobody can follow.
        Assert.Contains(job, opened.GetProperty("watch").GetString() ?? string.Empty,
            StringComparison.Ordinal);

        (string finished, string failure) = await SettledAsync(root, token!, job);

        // <b>Failed, and this is the assertion rather than a disappointment.</b> Four empty files are
        // not a geodatabase; a success would mean GDAL had read them as one.
        Assert.Equal("failed", finished);

        // <b>And a failure carries a reason, which `IJobStore` refuses to store without.</b> A job
        // saying only *failed* is the one thing nobody can act on.
        Assert.False(
            string.IsNullOrWhiteSpace(failure),
            "The job failed without a reason, which is the state the job store exists to prevent.");
    }

    /// <summary>
    /// Polls a job until it stops, and returns its status and failure.
    /// </summary>
    /// <remarks>
    /// <b>Thirty seconds, against a claim interval of two and a reader that answers in well under
    /// one.</b> Long enough that a loaded machine does not fail this, short enough that a job which
    /// never gets claimed is reported as such rather than hanging the suite — and the message says
    /// which of the two it was, because *the test timed out* is the least useful sentence a suite can
    /// produce.
    /// </remarks>
    private async Task<(string Status, string Failure)> SettledAsync(
        string root, string token, string job)
    {
        string last = "never asked";

        for (int attempt = 0; attempt < 30; attempt++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, $"{root}/admin/jobs/{job}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage response = await Http.SendAsync(request);

            JsonElement found = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync()).RootElement;

            last = found.GetProperty("status").GetString() ?? "unreadable";

            if (last is not ("queued" or "running"))
            {
                return (last, found.TryGetProperty("failure", out JsonElement why)
                    ? why.GetString() ?? string.Empty
                    : string.Empty);
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        Assert.Fail(
            $"Job {job} was still '{last}' after thirty seconds. 'queued' means the inspector never "
            + "claimed it — it does not run when the reader is missing, and it logs that once at "
            + "startup. 'running' means the reader was started and did not answer inside its own "
            + "two-minute deadline, which would be the first archive to do that.");

        return (last, string.Empty);
    }

    /// <summary>
    /// GeoPackage and KML are named too, and each cites the condition that deferred it.
    /// </summary>
    /// <remarks>
    /// <b>ADR-024 condition 3 is the subject.</b> *"A second archive format does not reuse this
    /// exception without its own ADR… because 'we already decompress' is not one."* The refusals say
    /// so, which is the difference between a deferral and a gap.
    /// </remarks>
    [Theory]
    [InlineData("cities.gpkg", "GeoPackage")]
    [InlineData("tour.kml", "KML")]
    public async Task A_deferred_archive_format_is_named_and_cites_its_condition(
        string member, string expected)
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        (HttpStatusCode status, string message) = await ImportAsync(
            root, token!, Zip((member, 64)), "probe.zip", "zz_deferred_refusal");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(expected, message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ADR-024", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A ZIP holding nothing recognisable still gets the generic refusal.
    /// </summary>
    /// <remarks>
    /// <b>The recogniser must not become the only answer.</b> If it matched broadly it would name a
    /// format for archives that hold none, and *"this is a GeoPackage"* about a folder of photographs
    /// is worse than *"the archive holds none of the files this import needs"*. So the fall-through is
    /// asserted, not assumed.
    /// </remarks>
    [Fact]
    public async Task An_unrecognised_archive_still_says_what_the_import_needs()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        (HttpStatusCode status, string message) = await ImportAsync(
            root, token!, Zip(("notes.txt", 32), ("photo.jpg", 64)), "probe.zip", "zz_unknown");

        Assert.Equal(HttpStatusCode.BadRequest, status);

        Assert.Contains(".shp", message, StringComparison.Ordinal);

        foreach (string named in new[] { "File Geodatabase", "GeoPackage", "KML" })
        {
            Assert.DoesNotContain(named, message, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ------------------------------------------------------------------------------ helpers

    /// <summary>
    /// Everything <c>POST /admin/hosted/geodatabase</c> refuses, refused before anything is written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One test with five claims, because they share an expensive fixture and nothing else.</b> Each
    /// needs an inspection job that belongs to this caller, and getting one means uploading an archive
    /// and waiting for a worker to claim it. Five tests would be five uploads of the same four empty
    /// files for five one-line assertions.
    /// </para>
    /// <para>
    /// <b>The archive is four empty files in a `roads.gdb/` folder, and its inspection fails.</b> That
    /// is what makes it the right fixture here: this endpoint's job is to refuse, and an inspection that
    /// failed is one of the things it must refuse to publish from. What it cannot test is the happy
    /// path — a real geodatabase is somebody's data and stays out of this repository, so the round trip
    /// is measured by hand instead and written down with its numbers
    /// ([file-geodatabase-readers.md](../../docs/research/file-geodatabase-readers.md) §8g).
    /// </para>
    /// <para>
    /// <b>Every refusal here is checked for a *reason*, not only a status.</b> A 400 that does not say
    /// which layer was not in the archive, or that a name is a URL segment, sends the operator to the
    /// server log — which is the failure mode this repository keeps finding in its own error paths.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Publishing_from_a_geodatabase_refuses_what_it_cannot_do()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        byte[] archive = Zip(
            ("roads.gdb/a00000001.gdbtable", 64),
            ("roads.gdb/a00000001.gdbtablx", 64),
            ("roads.gdb/a00000004.gdbindexes", 64),
            ("roads.gdb/gdb", 64));

        (HttpStatusCode opening, string opened) = await ImportAsync(
            root, token!, archive, "probe.gdb.zip", "zz_gdb_publish_refusals");

        Assert.True(
            opening == HttpStatusCode.Accepted,
            $"Expected 202 with a job. Got {(int)opening}: {opened}");

        string job = JsonDocument.Parse(opened).RootElement.GetProperty("job").GetString()!;

        // 1. No service name. The whole point of this endpoint is that N layers go into one service.
        (HttpStatusCode nameless, string why) = await PublishAsync(
            root, token!, $"{{\"archive\":\"{job}\",\"layers\":[\"anything\"]}}");

        Assert.Equal(HttpStatusCode.BadRequest, nameless);
        Assert.Contains("service", why, StringComparison.OrdinalIgnoreCase);

        // 2. A name that cannot be a URL segment. A service name is addressable or it is nothing.
        (HttpStatusCode slashed, string slashWhy) = await PublishAsync(
            root, token!, $"{{\"archive\":\"{job}\",\"service\":\"a/b\",\"layers\":[\"x\"]}}");

        Assert.Equal(HttpStatusCode.BadRequest, slashed);
        Assert.Contains("URL", slashWhy, StringComparison.OrdinalIgnoreCase);

        // 3. No layers. Publishing nothing is not a request with an outcome.
        (HttpStatusCode empty, string emptyWhy) = await PublishAsync(
            root, token!, $"{{\"archive\":\"{job}\",\"service\":\"zz_x\",\"layers\":[]}}");

        Assert.Equal(HttpStatusCode.BadRequest, empty);
        Assert.Contains("layers", emptyWhy, StringComparison.OrdinalIgnoreCase);

        // 4. An archive that is not a job of this caller's. Not found rather than forbidden, which is
        //    `IJobStore.FindAsync`'s own rule: a 403 on an id confirms the id.
        (HttpStatusCode absent, _) = await PublishAsync(
            root, token!,
            "{\"archive\":\"00000000-0000-0000-0000-000000000000\","
            + "\"service\":\"zz_x\",\"layers\":[\"x\"]}");

        Assert.Equal(HttpStatusCode.NotFound, absent);

        // 5. The inspection has to have finished before there is a list to choose from. This fixture's
        //    inspection fails — four empty files are not a geodatabase — so once it has settled, the
        //    refusal is about the inspection's state rather than about the layer name.
        (string settled, _) = await SettledAsync(root, token!, job);

        Assert.Equal("failed", settled);

        (HttpStatusCode unusable, string unusableWhy) = await PublishAsync(
            root, token!,
            $"{{\"archive\":\"{job}\",\"service\":\"zz_x\",\"layers\":[\"anything\"]}}");

        // <b>409, not 400</b>: nothing is wrong with the request, and the operator's next move is to
        // look at why the inspection failed rather than to change what they asked for.
        Assert.Equal(HttpStatusCode.Conflict, unusable);
        Assert.Contains("failed", unusableWhy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"/admin/jobs/{job}", unusableWhy, StringComparison.Ordinal);
    }

    private async Task<(HttpStatusCode Status, string Message)> PublishAsync(
        string root, string token, string body)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}/admin/hosted/geodatabase")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        string said = await response.Content.ReadAsStringAsync();
        string message = said;

        try
        {
            JsonElement answered = JsonDocument.Parse(said).RootElement;

            if (answered.TryGetProperty("error", out JsonElement error)
                && error.TryGetProperty("message", out JsonElement told))
            {
                message = told.GetString() ?? said;
            }
        }
        catch (JsonException)
        {
            // The raw body, which is more useful in a failure than an empty string.
        }

        return (response.StatusCode, message);
    }

    private static byte[] Zip(params (string Name, int Bytes)[] members)
    {
        using MemoryStream buffer = new();

        using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, int bytes) in members)
            {
                using Stream entry = zip.CreateEntry(name).Open();
                entry.Write(new byte[bytes]);
            }
        }

        return buffer.ToArray();
    }

    private async Task<(HttpStatusCode Status, string Message)> ImportAsync(
        string root, string token, byte[] archive, string filename, string name)
    {
        using MultipartFormDataContent form = new();
        using ByteArrayContent bytes = new(archive);

        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(bytes, "file", filename);
        form.Add(new StringContent(name), "name");

        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}/admin/hosted/import")
        {
            Content = form,
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        string body = await response.Content.ReadAsStringAsync();

        string message = body;

        try
        {
            JsonElement root2 = JsonDocument.Parse(body).RootElement;

            if (root2.TryGetProperty("error", out JsonElement error)
                && error.TryGetProperty("message", out JsonElement said))
            {
                message = said.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            // Left as the raw body, which is more useful in a failure than an empty string.
        }

        return (response.StatusCode, message);
    }
}
