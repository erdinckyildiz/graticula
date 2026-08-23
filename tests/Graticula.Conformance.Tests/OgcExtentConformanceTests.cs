using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A collection's own published extent selects every feature in it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one query guaranteed to match everything, and it matched nothing.</b> A collection
/// document publishes its extent in CRS84 whatever the layer is stored in — Part 1 §7.13 — and a
/// client's first move is to send that box back as a <c>bbox</c>. For a layer stored in a
/// projected reference system that box was made by projecting the data one way and is filtered by
/// projecting back the other, so the two disagree in the last few digits, every feature lands
/// exactly on an edge, and every edge test fails. Three of five layers answered a client's own
/// extent with an empty collection.
/// </para>
/// <para>
/// <b>Repaired twice, and the first repair is why this test exists.</b> The filter was widened
/// whenever it had to be transformed, which fixed this and made the same box mean two different
/// things depending on which reference system it was written in — the CITE suite's
/// <c>verifyBboxCrsParameter</c> catches exactly that, and did. Q-133 chose the other repair:
/// publish the extent rounded outward and compare every filter exactly.
/// </para>
/// <para>
/// <b>And the second repair was wrong once too, in a way only a measurement found.</b> Rounding
/// at the point of printing left the filter path clamping the request to the *un*rounded extent,
/// which erased the rounding before the transformation and put the defect straight back: the
/// six-feature layer answered its own extent with four. The invariant is that <b>the extent a
/// client is given is the extent the server clamps to</b>, and this test is the only thing that
/// states it from outside.
/// </para>
/// </remarks>
public sealed class OgcExtentConformanceTests : ArcGisClient
{
    private const string Root = "/ogc/features/v1";

    private const string Crs84 = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";

    private static string Box(JsonElement bbox) =>
        string.Join(",", bbox.EnumerateArray().Select(
            v => v.GetDouble().ToString("R", CultureInfo.InvariantCulture)));

    /// <summary>Every collection, with its published extent and its feature count.</summary>
    private async Task<IReadOnlyList<(string Id, string Extent, int Matched)>> CollectionsAsync()
    {
        JsonElement document = await GetJsonAsync($"{Root}/collections");

        List<(string, string, int)> found = [];

        foreach (JsonElement collection in
            Require(document, "collections", "the collections document lists them").EnumerateArray())
        {
            string id = collection.GetProperty("id").GetString()!;

            JsonElement bbox = collection
                .GetProperty("extent").GetProperty("spatial").GetProperty("bbox")[0];

            // <b>The count comes from the server rather than from this test.</b> numberMatched is
            // separately verified below by walking every page, so using it here is not circular:
            // if it were wrong, the walk would say so.
            JsonElement all = await GetJsonAsync($"{Root}/collections/{id}/items?limit=1");

            found.Add((id, Box(bbox), all.GetProperty("numberMatched").GetInt32()));
        }

        Assert.NotEmpty(found);
        return found;
    }

    /// <summary>
    /// Asking a collection for its own extent returns all of it.
    /// </summary>
    [Fact]
    public async Task A_collections_own_extent_selects_every_feature_in_it()
    {
        await RequireServerAsync();

        List<string> wrong = [];

        foreach ((string id, string extent, int matched) in await CollectionsAsync())
        {
            // <b>numberMatched, not the features in the page, and the first version of this test
            // got that wrong.</b> It asked for `limit = matched + 1` so that one page would carry
            // everything — and the server caps a page at 1,000, so four collections reported
            // "selects 1000 of 5433" and the test accused the filter of a defect that was its
            // own arithmetic. numberMatched is a count of the filter's result and says nothing
            // about the page, which is exactly the claim being made here; that it is truthful is
            // asserted separately by walking every page.
            JsonElement page = await GetJsonAsync(
                $"{Root}/collections/{id}/items?bbox={extent}&limit=1");

            int selected = page.GetProperty("numberMatched").GetInt32();

            /*
              <b>Against the features that have a location, not against every row — and this
              test was wrong about that until a corpus fixture with a null shape arrived.</b>
              A feature with no geometry cannot intersect any bounding box, so a collection
              holding one can never have its extent select all of it. The old assertion read
              *its own extent selects 1 of 2* and blamed the filter for the one thing a
              filter is right about.

              <b>The second count is asked for only when the first falls short</b>, so the
              healthy case — every collection here but one — still costs one request.
            */
            int located = ShortOfItself(matched, selected)
                ? await LocatedAsync(id)
                : matched;

            if (selected != located)
            {
                wrong.Add(
                    $"{id}: its own extent [{extent}] selects {selected} of {located} located "
                    + $"features, out of {matched} rows");
            }
        }

        Assert.True(
            wrong.Count == 0,
            "A collection's published extent must contain its own features. It is the query a "
            + "client makes first and the one that cannot legitimately be empty:\n  "
            + string.Join("\n  ", wrong));
    }

    /// <summary>
    /// Whether a shortfall could be explained by rows with no geometry.
    /// </summary>
    /// <remarks>
    /// <b>Asked before the extra request, so the healthy case costs nothing.</b> Almost
    /// every collection selects all of itself; only one that does not is worth a second
    /// query.
    /// </remarks>
    private static bool ShortOfItself(int matched, int selected) => selected < matched;

    /// <summary>How many of a collection's features have a location at all.</summary>
    /// <remarks>
    /// <b>The whole world as a bounding box.</b> Every located feature is inside it and no
    /// unlocated one is, which makes this the count the extent assertion is really about.
    /// </remarks>
    private async Task<int> LocatedAsync(string id)
    {
        JsonElement everywhere = await GetJsonAsync(
            $"{Root}/collections/{id}/items?bbox=-180,-90,180,90&limit=1");

        return everywhere.GetProperty("numberMatched").GetInt32();
    }

    /// <summary>
    /// The same box in two reference systems selects the same features.
    /// </summary>
    /// <remarks>
    /// <b>This is what the widened filter broke</b>, and it is worth asserting here rather than
    /// only in the CITE suite: that suite is run by hand on one machine and D-63 means nothing
    /// runs it again. The projected box is derived from the features the server itself returns in
    /// its storage reference system, so no projection library is involved on this side.
    /// </remarks>
    [Fact]
    public async Task One_box_in_two_reference_systems_selects_the_same_features()
    {
        await RequireServerAsync();

        List<string> disagreed = [];
        int compared = 0;

        foreach ((string id, string extent, int matched) in await CollectionsAsync())
        {
            JsonElement collection = await GetJsonAsync($"{Root}/collections/{id}");
            string storage = collection.GetProperty("storageCrs").GetString()!;

            if (storage == Crs84 || matched > 200)
            {
                // A CRS84 layer has nothing to compare, and a large collection would page.
                continue;
            }

            JsonElement native = await GetJsonAsync(
                $"{Root}/collections/{id}/items?limit={matched + 1}"
                + $"&crs={Uri.EscapeDataString(storage)}");

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach ((double x, double y) in native.GetProperty("features").EnumerateArray()
                .SelectMany(f => Coordinates(f.GetProperty("geometry"))))
            {
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            if (minX > maxX)
            {
                continue;
            }

            string box = string.Join(",", new[] { minX, minY, maxX, maxY }
                .Select(v => v.ToString("R", CultureInfo.InvariantCulture)));

            string[] geographic = Ids(await GetJsonAsync(
                $"{Root}/collections/{id}/items?bbox={extent}"
                + $"&bbox-crs={Uri.EscapeDataString(Crs84)}&limit={matched + 1}"));

            string[] projected = Ids(await GetJsonAsync(
                $"{Root}/collections/{id}/items?bbox={box}"
                + $"&bbox-crs={Uri.EscapeDataString(storage)}&limit={matched + 1}"));

            compared++;

            if (!geographic.SequenceEqual(projected))
            {
                disagreed.Add(
                    $"{id}: CRS84 selects {geographic.Length}, {storage} selects "
                    + $"{projected.Length}");
            }
        }

        Assert.True(compared > 0, "No collection was stored in anything but CRS84, so this test "
            + "asserted nothing. It needs a layer in a projected reference system.");

        Assert.True(
            disagreed.Count == 0,
            "A bounding box means the same thing however it is spelled, which is what "
            + "verifyBboxCrsParameter asks and what an epsilon on one path only breaks:\n  "
            + string.Join("\n  ", disagreed));
    }

    /// <summary>
    /// Walking every page of a collection yields exactly the number it says it matched.
    /// </summary>
    /// <remarks>
    /// <b>Written because a CITE run said the opposite and was wrong.</b> Six assertions failed
    /// with *numberMatched (5433) does not match the number of features in all responses (50)*,
    /// and the request log showed the suite had asked for five pages and stopped — its own bound,
    /// met by a collection larger than it will page through. A red result from an external suite
    /// is evidence and not a verdict, and this is the check that settles which.
    /// </remarks>
    [Fact]
    public async Task Walking_every_page_yields_the_number_the_collection_matched()
    {
        await RequireServerAsync();

        List<string> wrong = [];

        string? edited = Environment.GetEnvironmentVariable("GRATICULA_TEST_EDITABLE");

        foreach ((string id, _, int matched) in await CollectionsAsync())
        {
            if (matched > 2000)
            {
                // Paging 2,000 features proves what 200 proves, at a hundred times the cost.
                continue;
            }

            /*
              <b>Not the layer other suites write to, and this was found by a failing run
              rather than foreseen.</b> *walked 3 distinct features over 1 pages,
              numberMatched says 4*: the edit suite inserted a feature between this test's
              count and its walk. Nothing is wrong with either number — they were taken a
              moment apart from a layer that was changing.

              <b>A test that reads a moving count twice cannot be made reliable by
              retrying</b>, which is why this excludes the layer by the same name the
              editing suite is given rather than tolerating a mismatch. Tolerating one would
              also tolerate the defect this test exists to catch.
            */
            if (edited is { Length: > 0 }
                && edited.EndsWith(id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            HashSet<string> seen = [];
            string? next = $"{Root}/collections/{id}/items?limit=10";
            int pages = 0;

            while (next is not null && pages < 500)
            {
                JsonElement page = await GetJsonAsync(next);
                pages++;

                foreach (string identifier in Ids(page))
                {
                    seen.Add(identifier);
                }

                next = null;

                foreach (JsonElement link in page.GetProperty("links").EnumerateArray())
                {
                    if (link.GetProperty("rel").GetString() != "next")
                    {
                        continue;
                    }

                    // Relative, because the absolute one names the host the server thinks it is
                    // and this suite has reached it by a different name more than once.
                    string href = link.GetProperty("href").GetString()!;
                    int cut = href.IndexOf(Root, StringComparison.Ordinal);
                    next = cut < 0 ? href : href[cut..];
                    break;
                }
            }

            if (seen.Count != matched)
            {
                wrong.Add($"{id}: walked {seen.Count} distinct features over {pages} pages, "
                    + $"numberMatched says {matched}");
            }
        }

        Assert.True(
            wrong.Count == 0,
            "numberMatched is a promise about what paging will yield:\n  "
            + string.Join("\n  ", wrong));
    }

    private static string[] Ids(JsonElement page) =>
        [.. page.GetProperty("features").EnumerateArray()
            .Select(f => f.TryGetProperty("id", out JsonElement id)
                ? id.ToString()
                : string.Empty)
            .OrderBy(v => v, StringComparer.Ordinal)];

    /// <summary>Every coordinate pair in a GeoJSON geometry, however deeply nested.</summary>
    private static IEnumerable<(double X, double Y)> Coordinates(JsonElement geometry)
    {
        if (geometry.ValueKind != JsonValueKind.Object
            || !geometry.TryGetProperty("coordinates", out JsonElement coordinates))
        {
            yield break;
        }

        Stack<JsonElement> pending = new();
        pending.Push(coordinates);

        while (pending.Count > 0)
        {
            JsonElement current = pending.Pop();

            if (current.ValueKind != JsonValueKind.Array || current.GetArrayLength() == 0)
            {
                continue;
            }

            if (current[0].ValueKind == JsonValueKind.Number)
            {
                yield return (current[0].GetDouble(), current[1].GetDouble());
                continue;
            }

            foreach (JsonElement part in current.EnumerateArray())
            {
                pending.Push(part);
            }
        }
    }
}
