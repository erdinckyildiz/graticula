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
    /// A File Geodatabase is named, and pointed at the decision that owns it.
    /// </summary>
    /// <remarks>
    /// <b>The archive is built here rather than committed.</b> Four empty members in a
    /// <c>roads.gdb/</c> folder is enough to be recognised, and it carries no Esri bytes — which
    /// matters: a real geodatabase in the test corpus would be somebody's data, and the recogniser
    /// reads entry names only, so a real one would test nothing extra.
    /// </remarks>
    [Fact]
    public async Task A_file_geodatabase_is_refused_by_name_and_points_at_the_question()
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
            root, token!, archive, "probe.gdb.zip", "zz_gdb_refusal");

        Assert.Equal(HttpStatusCode.BadRequest, status);

        // Named, so the reader knows what they sent.
        Assert.Contains("File Geodatabase", message, StringComparison.Ordinal);

        // <b>And pointed somewhere.</b> A refusal that says *not supported* and stops leaves somebody
        // guessing whether it is coming; this one names the open question and the note behind it.
        Assert.Contains("Q-108", message, StringComparison.Ordinal);

        // <b>And says what does work.</b> Otherwise the next thing they try is another guess.
        Assert.Contains("shapefile", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GeoJSON", message, StringComparison.OrdinalIgnoreCase);

        // <b>What must not survive: the folder advice.</b> That is the sentence this test exists to
        // keep out of a geodatabase's refusal, and it is still correct for a genuinely nested
        // shapefile — so it is asserted absent here rather than deleted there.
        Assert.DoesNotContain("zip the shapefile's files directly", message, StringComparison.OrdinalIgnoreCase);
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
