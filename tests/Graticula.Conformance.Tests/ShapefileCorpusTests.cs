using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Every shapefile in the corpus imports, and produces what it produced before.
/// </summary>
/// <remarks>
/// <para>
/// <b>The corpus is the evidence [D-113](../../docs/architecture-debt.md) needed, so it
/// moves here rather than being retired with the parser it was written for.</b> That debt is
/// the §66 simplicity gate's disqualifying finding: ADR-037 §5a moved GDAL into a child
/// process because it *"removes an untrusted-file parser from the process that serves public
/// requests"*, and 1,094 lines of our own shapefile parser went on running inside it. The
/// parse is in the child process now, and what these files knew about shapefiles —
/// winding order, multi-part records, null shapes, three character sets, real OSM polygons —
/// has to survive the move or the move lost something.
/// </para>
/// <para>
/// <b>Through the live server rather than against a class</b>, which is what makes this a
/// replacement rather than a translation. The old tests called
/// <c>ShapefileReader.TryRead</c>; there is nothing on this side of the boundary to call
/// any more, and an assertion about what an operator gets when they upload a file is the
/// stronger one anyway.
/// </para>
/// <para>
/// <b>One expectation changed and it is recorded rather than absorbed.</b> A shapefile
/// polygon record holding a single ring used to publish as <c>MultiPolygon</c>, because the
/// old parser wrapped every polygon record; GDAL reports it as <c>Polygon</c>. Both are true
/// readings and the narrower one is better — a single polygon in a Polygon column is what it
/// is — but it is a visible change to a layer document and the table below says so.
/// </para>
/// </remarks>
public sealed class ShapefileCorpusTests : ArcGisClient
{
    /// <summary>
    /// What each archive should turn into.
    /// </summary>
    /// <remarks>
    /// <b>Carried over from <c>ShapefileReaderTests</c>, one line per thing that file
    /// knew.</b> The row count and the field list come from its own assertions; the
    /// geometry type is the one place they differ, marked where it does.
    /// </remarks>
    public static TheoryData<string, string, int, string> Corpus => new()
    {
        // archive, geometry type, rows, comma-separated name:type
        { "points", "Point", 2, "id:Integer,name:Text,value:Double" },

        // <b>Changed: was MultiPolygon.</b> One record, one exterior ring, one hole. The
        // old parser wrapped it; GDAL does not, and a Polygon column holds it exactly.
        { "holed", "Polygon", 1, "label:Text" },

        // Two exterior rings in one record really are two polygons, which is the
        // winding-order rule the corpus exists for.
        { "twoparts", "MultiPolygon", 1, "label:Text" },

        { "lines", "MultiLineString", 2, "label:Text" },

        // A null shape is a feature with no location, not a row to drop.
        { "withnull", "Point", 2, "label:Text" },

        // <b>Changed: was MultiPolygon, and this is the same change as `holed` above.</b>
        // With GDAL organising rings by containment, all fifty of these real OSM polygons
        // are one shell — some with holes — so the layer is a Polygon layer. **The
        // validity is what matters here and it is unchanged at 50 of 50**: without that
        // containment analysis PostGIS reported 47, *hole lies outside shell; nested
        // shells*, which is the exact failure the parser this replaced was written to
        // avoid and whose number its own tests record.
        { "osm_real", "Polygon", 50, "name:Text" },

        // Two archives with the same text in different character sets, both declared.
        { "turkish_utf8", "Point", 2, "ad:Text" },
        { "turkish_cp1254", "Point", 2, "ad:Text" },
    };

    private static string CorpusDirectory()
    {
        DirectoryInfo? at = new(AppContext.BaseDirectory);

        while (at is not null && !Directory.Exists(Path.Combine(at.FullName, "tests")))
        {
            at = at.Parent;
        }

        Assert.NotNull(at);

        string path = Path.Combine(
            at!.FullName, "tests", "Graticula.Core.Tests", "corpus", "shapefile");

        Assert.True(
            Directory.Exists(path),
            $"The shapefile corpus was not found at '{path}'. These tests are the corpus's "
            + "only remaining reader, so a path that stops resolving loses it silently.");

        return path;
    }

    private async Task<(HttpStatusCode Status, JsonDocument Body)> ImportAsync(
        string archive, string name)
    {
        string root = await RequireServerAsync();

        string file = Path.Combine(CorpusDirectory(), archive + ".zip");

        Assert.True(File.Exists(file), $"'{file}' is missing from the corpus.");

        using MultipartFormDataContent form = new();

        using ByteArrayContent bytes = new(await File.ReadAllBytesAsync(file));
        form.Add(bytes, "file", archive + ".zip");
        form.Add(new StringContent(name), "name");
        form.Add(new StringContent("4326"), "srid");

        using HttpRequestMessage request = new(HttpMethod.Post, root + "/admin/hosted")
        {
            Content = form,
        };

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (
            response.StatusCode,
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()));
    }

    /// <summary>Removes what a case published, whichever way the case went.</summary>
    /// <remarks>
    /// <para>
    /// <b>The route is <c>/admin/featureservices/{name}</c> and the first version of this
    /// used <c>/admin/hosted/{name}</c>, which does not exist.</b> Nothing failed visibly:
    /// the delete answered 404, the test passed, and the imports accumulated. Three tests
    /// in other classes then failed — a paging test on a one-feature leftover, a CRS test
    /// on the same, and an extent test on a leftover holding a null geometry — which is
    /// [D-75](../../docs/architecture-debt.md)'s family with this class as the cause.
    /// </para>
    /// <para>
    /// <b>So the removal is asserted.</b> A cleanup that can fail silently is a cleanup
    /// that will, and the suite that pays for it is somebody else's.
    /// </para>
    /// </remarks>
    private async Task RemoveAsync(string name)
    {
        string root = await RequireServerAsync();

        // <b>The folder and the drop, both of which the first version left out.</b> An
        // import lands in `hosted`, and the route takes the bare name plus a folder — so a
        // delete without it answered 404 and the table stayed behind as well as the
        // service. `drop=true` removes the table, because a corpus fixture's table is
        // nobody's data.
        using HttpRequestMessage request = new(
            HttpMethod.Delete,
            $"{root}/admin/featureservices/{name}?folder=hosted&drop=true");

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent
                or HttpStatusCode.NotFound,
            $"Removing '{name}' answered {(int)response.StatusCode}. A leftover service "
            + "poisons every other suite that walks the catalogue: "
            + await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// An archive from the corpus publishes with the shape the corpus expects.
    /// </summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task A_corpus_archive_publishes_what_the_corpus_expects(
        string archive, string geometry, int rows, string fields)
    {
        // <b>A name per run, because a previous run's leftover would make this pass by
        // colliding.</b> Removed afterwards whichever way the assertions go.
        string name = $"corpus_{archive}_{Guid.NewGuid():N}"[..Math.Min(40, 8 + archive.Length + 33)];

        try
        {
            (HttpStatusCode status, JsonDocument body) = await ImportAsync(archive, name);

            using (body)
            {
                Assert.Equal(HttpStatusCode.Created, status);

                JsonElement root = body.RootElement;

                Assert.Equal(geometry, root.GetProperty("geometryType").GetString());
                Assert.Equal(rows, root.GetProperty("rows").GetInt32());

                string got = string.Join(
                    ",",
                    root.GetProperty("fields").EnumerateArray().Select(
                        f => f.GetProperty("name").GetString()
                            + ":" + f.GetProperty("type").GetString()));

                Assert.Equal(fields, got);

                // <b>And every geometry is valid, which the old corpus asserted per
                // feature.</b> The import reports it, so this is the same claim read from
                // the answer instead of from the parse.
                Assert.True(
                    root.GetProperty("geometry").GetProperty("valid").GetBoolean(),
                    root.GetProperty("geometry").GetProperty("note").GetString());
            }
        }
        finally
        {
            await RemoveAsync(name);
        }
    }

    /// <summary>
    /// A .dbf with high bytes and no declared character set is refused, not guessed.
    /// </summary>
    /// <remarks>
    /// <b>Owner decision, Q-98, and it is the one refusal in the corpus.</b> Reading
    /// Windows-1254 bytes as UTF-8 does not throw and does not look broken at a glance —
    /// it produces a string, and the damage surfaces months later in somebody's map labels.
    /// Measured through the child process as well: GDAL reads that archive as mojibake
    /// unless told the encoding, so the refusal is doing real work rather than duplicating
    /// a guard GDAL already has.
    /// </remarks>
    [Fact]
    public async Task An_undeclared_character_set_is_refused_rather_than_guessed()
    {
        string name = $"corpus_undeclared_{Guid.NewGuid():N}"[..40];

        try
        {
            (HttpStatusCode status, JsonDocument body) =
                await ImportAsync("turkish_undeclared", name);

            using (body)
            {
                Assert.Equal(HttpStatusCode.BadRequest, status);

                string? said = body.RootElement
                    .GetProperty("error").GetProperty("message").GetString();

                Assert.Contains(".cpg", said!, StringComparison.Ordinal);
            }
        }
        finally
        {
            await RemoveAsync(name);
        }
    }

    /// <summary>
    /// An adversarial archive is refused before anything parses it.
    /// </summary>
    /// <remarks>
    /// <b>Our bounds, not GDAL's, and the order matters.</b> `BoundedArchive` reads the ZIP
    /// directory and refuses these three without expanding them; GDAL also refuses them,
    /// measured, so the bounds are belt and braces rather than the only guard. What they
    /// buy is that a bomb never reaches the child process at all.
    /// </remarks>
    [Theory]
    [InlineData("bomb")]
    [InlineData("nested")]
    [InlineData("russian_doll")]
    public async Task An_adversarial_archive_is_refused(string archive)
    {
        string name = $"corpus_bad_{Guid.NewGuid():N}"[..40];

        try
        {
            (HttpStatusCode status, JsonDocument body) = await ImportAsync(archive, name);

            using (body)
            {
                Assert.Equal(HttpStatusCode.BadRequest, status);
            }
        }
        finally
        {
            await RemoveAsync(name);
        }
    }
}
