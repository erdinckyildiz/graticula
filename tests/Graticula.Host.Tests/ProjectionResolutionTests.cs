using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Graticula.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A shapefile's own projection resolves to an EPSG code, so the operator need not be asked.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-024](../../docs/adr/ADR-024-shapefile-import.md) made <c>srid</c> mandatory, and the reason
/// is intact:</b> a `.prj` is bare WKT, and matching WKT to a code **by comparing strings** is how a
/// layer comes to declare a system it is not in. What changed on 2026-08-19 is that this product now
/// contains a projection database — the reader resolves a coordinate system through PROJ's authority
/// tables, which is what the geodatabase path has done since it was built. Asking an authority is not
/// guessing.
/// </para>
/// <para>
/// <b>The owner's observation is what prompted it:</b> the reference asks for no coordinate system
/// anywhere in its upload flow, and ours refused without one. Both cannot be right about the same
/// file.
/// </para>
/// <para>
/// <b>The corpus is Esri dialect, which is the case that matters.</b> `tools/make-shapefile-corpus.py`
/// writes `GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",…]]]` — what ArcGIS writes,
/// not what OGC specifies. A resolver that only handled OGC spelling would pass a hand-written test and
/// fail on every file a customer has.
/// </para>
/// <para>
/// <b>What this does not cover, stated rather than left to be discovered:</b> every archive in the
/// corpus is geographic and every one is 4326. A **projected** Esri `.prj` — the owner's own data is
/// EPSG:2952 — is untested, because this project has no such shapefile it did not write and a
/// hand-written WKT would make a negative result uninterpretable. That gap is why the endpoint still
/// refuses when PROJ has no answer, rather than assuming one.
/// </para>
/// </remarks>
public sealed class ProjectionResolutionTests
{
    /// <summary>Where the shapefile corpus lives, from wherever this assembly is running.</summary>
    private static string Corpus()
    {
        DirectoryInfo? at = new(AppContext.BaseDirectory);

        while (at is not null && !File.Exists(Path.Combine(at.FullName, "CLAUDE.md")))
        {
            at = at.Parent;
        }

        Assert.NotNull(at);

        return Path.Combine(
            at!.FullName, "tests", "Graticula.Core.Tests", "corpus", "shapefile");
    }

    /// <summary>
    /// Every geographic archive in the corpus resolves, and resolves to the code it declares.
    /// </summary>
    /// <remarks>
    /// <b>A theory rather than a loop, so a failure names the archive.</b> `turkish_cp1254` is in the
    /// list on purpose: its attribute encoding is the thing that file exists to test, and a resolver
    /// that read the `.dbf` when it meant to read the `.prj` would fail there first.
    /// </remarks>
    [Theory]
    [InlineData("points.zip")]
    [InlineData("lines.zip")]
    [InlineData("holed.zip")]
    [InlineData("osm_real.zip")]
    [InlineData("turkish_cp1254.zip")]
    [InlineData("twoparts.zip")]
    public async Task An_esri_dialect_prj_resolves_to_its_epsg_code(string archive)
    {
        string path = Path.Combine(Corpus(), archive);

        Assert.True(
            File.Exists(path),
            $"The corpus archive '{archive}' is not at {path}. It is built by "
            + "tools/make-shapefile-corpus.py.");

        GeodatabaseReader reader = new(
            GeodatabaseReader.ExecutableBesideThisOne(), NullLogger<GeodatabaseReader>.Instance);

        using JsonDocument answer = await reader.AskAsync(
            new { op = "layers", archive = path }, TimeSpan.FromSeconds(30));

        string refusal = answer.RootElement.TryGetProperty("error", out JsonElement why)
            ? why.GetString() ?? "no reason given"
            : "no reason given";

        Assert.True(
            answer.RootElement.GetProperty("ok").GetBoolean(),
            $"The reader could not open '{archive}': {refusal}");

        JsonElement layers = answer.RootElement.GetProperty("layers");

        Assert.True(layers.GetArrayLength() > 0, $"'{archive}' reported no layers.");

        JsonElement first = layers[0];

        Assert.True(
            first.TryGetProperty("srid", out JsonElement srid)
            && srid.ValueKind == JsonValueKind.Number,
            $"'{archive}' resolved no coordinate system. Its .prj is Esri dialect — "
            + "GEOGCS[\"GCS_WGS_1984\",DATUM[\"D_WGS_1984\",…]] — and if PROJ has stopped identifying "
            + "that spelling then the import endpoint is back to demanding a code from the operator, "
            + "which is what the owner asked us to stop doing.");

        Assert.Equal(4326, srid.GetInt32());
    }

    /// <summary>
    /// An archive with no projection at all: what GDAL actually says, measured rather than assumed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The first version of this test was vacuous and said the opposite of the truth.</b> It used
    /// `withnull.zip` on the assumption that the name meant *no projection* — it means null attribute
    /// values, and the corpus tool writes it with a WGS 84 `.prj` like the rest. So the test resolved
    /// 4326, I read that as *GDAL invents a projection*, and it was simply reporting the projection the
    /// file declares. Three of the corpus archives genuinely have no `.prj` — `bomb`, `nested`,
    /// `russian_doll` — and all three are adversarial shapes the reader refuses for other reasons, so
    /// none of them can answer this either.
    /// </para>
    /// <para>
    /// <b>So the archive is built here: a real shapefile with its `.prj` left out.</b> The three
    /// members that make a shapefile, copied from the corpus, zipped without the fourth. That is the
    /// only way to ask the question, and the question matters: if OGR assumes WGS 84 when nothing
    /// declares it, then resolving would publish a layer declaring a system nobody put in the file —
    /// the exact sentence [ADR-024](../../docs/adr/ADR-024-shapefile-import.md) exists to prevent.
    /// </para>
    /// <para>
    /// <b>Whatever the answer, the endpoint's gate stands.</b> `HostedDataEndpoints` resolves only when
    /// `bundle.Prj` is not null, so an archive with no declaration is never asked about. This test says
    /// which of the two reasons that gate has: belt and braces, or load bearing.
    /// </para>
    /// <para>
    /// <b>The answer, as of 2026-08-19: belt and braces.</b> GDAL reports no coordinate system for a
    /// shapefile with no `.prj` — it does not assume WGS 84, which is what I had asserted in a comment
    /// before measuring it. The gate stays anyway: it is free, and it saves a child process on a path
    /// that was going to be refused.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task What_gdal_says_about_an_archive_with_no_prj_is_recorded_here()
    {
        string corpus = Corpus();
        string made = Path.Combine(Path.GetTempPath(), $"graticula_noprj_{Guid.NewGuid():N}.zip");

        using (FileStream file = File.Create(made))
        using (System.IO.Compression.ZipArchive zip = new(
            file, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (string part in new[] { "points.shp", "points.shx", "points.dbf" })
            {
                string from = Path.Combine(corpus, part);

                Assert.True(File.Exists(from), $"The corpus is missing {part} at {from}.");

                using Stream into = zip.CreateEntry(part).Open();
                using FileStream reading = File.OpenRead(from);

                await reading.CopyToAsync(into);
            }
        }

        try
        {
            GeodatabaseReader reader = new(
                GeodatabaseReader.ExecutableBesideThisOne(), NullLogger<GeodatabaseReader>.Instance);

            using JsonDocument answer = await reader.AskAsync(
                new { op = "layers", archive = made }, TimeSpan.FromSeconds(30));

            if (!answer.RootElement.GetProperty("ok").GetBoolean())
            {
                // A refusal is also *no code*, and the endpoint treats it the same way.
                return;
            }

            JsonElement layers = answer.RootElement.GetProperty("layers");

            Assert.True(layers.GetArrayLength() > 0, "The archive opened and reported no layers.");

            bool invented = layers[0].TryGetProperty("srid", out JsonElement srid)
                && srid.ValueKind == JsonValueKind.Number;

            Assert.False(
                invented,
                $"GDAL reported EPSG:{(invented ? srid.GetInt32() : 0)} for a shapefile with no .prj. "
                + "That makes the gate in HostedDataEndpoints — resolve only when bundle.Prj is not "
                + "null — load bearing rather than belt and braces, and this assertion is where to "
                + "record it: invert this test, keep the gate, and say so in ADR-024. What must never "
                + "happen is the resolver being trusted without the gate.");
        }
        finally
        {
            try { File.Delete(made); } catch (IOException) { /* a temp file, and not worth a failure. */ }
        }
    }
}
