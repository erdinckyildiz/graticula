using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Whether the documents agree with each other.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the half that catches real bugs.</b> Each document can be
/// individually correct while the set is incoherent, and a client trusts the
/// coherence: it reads the layer document once, builds its renderer and its
/// attribute table from it, and then assumes every feature matches. When they
/// disagree, the failure appears far from the cause — a blank attribute column,
/// a selection that never matches, a value silently rounded.
/// </para>
/// <para>
/// Two bugs already found by hand were exactly this shape: an object id declared
/// <c>esriFieldTypeOID</c> and emitted as a quoted string, and a
/// <c>objectIdFieldName</c> naming a field the features did not carry. Both
/// passed every test that looked at one document.
/// </para>
/// </remarks>
[Trait("Category", "Conformance")]
[Collection("catalogue walk")]
public sealed class ArcGisConsistencyTests : ArcGisClient
{
    /// <summary>
    /// The six consistency claims, asked of every layer this server serves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every layer, and that is the correction of 2026-08-18.</b> These asked their
    /// question of whichever service the catalogue listed first, which made their coverage
    /// a fact about row order rather than about the server. It cost a real defect: the
    /// paging test below passed for four days while three of the owner's ten layers were
    /// skipping rows, because the layer it landed on was small enough for heap order to be
    /// identity order (D-21).
    /// </para>
    /// <para>
    /// <b>Six claims rather than six tests per layer, because a failure has to name the
    /// layer.</b> Each check runs inside the loop and puts the qualified service name in
    /// its message — a red assertion that says *the object id was a string* without saying
    /// where is a red assertion somebody has to reproduce before they can act on it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Every_layer_is_consistent_with_its_own_document()
    {
        int examined = 0;

        foreach ((string name, JsonElement layer, JsonElement query) in
            await EveryLayerAndQueryAsync())
        {
            // A client selects and pages by this name. If they differ it matches
            // nothing, silently, forever.
            Assert.Equal(
                layer.GetProperty("objectIdField").GetString(),
                query.GetProperty("objectIdFieldName").GetString());

            // The client has already built a polygon renderer by the time the first
            // feature arrives.
            Assert.Equal(
                layer.GetProperty("geometryType").GetString(),
                query.GetProperty("geometryType").GetString());

            int declaredSrid = layer.GetProperty("extent").ValueKind == JsonValueKind.Null
                ? query.GetProperty("spatialReference").GetProperty("wkid").GetInt32()
                : layer.GetProperty("extent").GetProperty("spatialReference")
                    .GetProperty("wkid").GetInt32();

            Assert.Equal(
                declaredSrid,
                query.GetProperty("spatialReference").GetProperty("wkid").GetInt32());

            string oid = layer.GetProperty("objectIdField").GetString()!;

            JsonElement attributes = query.GetProperty("features")[0].GetProperty("attributes");

            // The bug this pins, found by reading a real response: objectid came out as
            // "1" in quotes while the field was declared esriFieldTypeOID. A client paging
            // or selecting against a quoted value matches nothing and is told nothing.
            Assert.True(
                attributes.TryGetProperty(oid, out JsonElement identity),
                $"'{name}' does not carry '{oid}' in its features, which objectIdFieldName "
                + "names. A client cannot page or select against a field that is not in the "
                + "response.");

            Assert.Equal(JsonValueKind.Number, identity.ValueKind);

            Dictionary<string, string> declared = layer.GetProperty("fields").EnumerateArray()
                .ToDictionary(
                    f => f.GetProperty("name").GetString()!,
                    f => f.GetProperty("type").GetString()!);

            foreach (JsonProperty attribute in attributes.EnumerateObject())
            {
                // An undeclared attribute has no column in the client's table, so the value
                // is fetched, transferred, and dropped.
                Assert.True(
                    declared.ContainsKey(attribute.Name),
                    $"'{name}' returned '{attribute.Name}' in a feature and it is not in the "
                    + "layer's field list.");

                if (attribute.Value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                // The silent class of failure: a field declared as an integer and sent as a
                // string parses back as a number and loses precision; declared as a number
                // and sent as a string sorts as text. Neither raises anything.
                string type = declared[attribute.Name];
                JsonValueKind kind = attribute.Value.ValueKind;

                bool agrees = type switch
                {
                    "esriFieldTypeOID" or "esriFieldTypeInteger" or "esriFieldTypeSmallInteger"
                        or "esriFieldTypeDouble" or "esriFieldTypeSingle" or "esriFieldTypeDate"
                        => kind == JsonValueKind.Number,
                    "esriFieldTypeString" or "esriFieldTypeGUID" or "esriFieldTypeBlob"
                        => kind == JsonValueKind.String,
                    _ => true,
                };

                Assert.True(
                    agrees,
                    $"'{name}.{attribute.Name}' is declared {type} and was sent as {kind}. A "
                    + "client parses the value according to the declaration, so this is wrong in "
                    + "a way nothing reports.");
            }

            examined++;
        }

        Assert.True(
            examined > 0,
            "No layer on this server returned a feature, so none of these claims was checked.");
    }

    [Fact]
    public async Task The_advertised_maximum_is_not_exceeded_by_a_request_for_more()
    {
        // A client that respects maxRecordCount never triggers a server-side
        // clamp. One that ignores it must still not be able to ask for more than
        // the server said it would give.
        //
        // <b>Every layer, because each service carries its own ceiling (ADR-031)</b> — so
        // these are genuinely different claims, and asking only the first one is the
        // coverage problem that let D-21's paging defect live for four days.
        foreach (string name in await EveryServiceNameAsync())
        {
            // A service deleted between the listing and this request is skipped; one that 404s
            // while still catalogued fails inside AboutServiceAsync, named. D-89.
            if (await AboutServiceAsync(name, $"/rest/services/{name}/FeatureServer/0")
                is not { } layer)
            {
                continue;
            }

            int advertised = layer.GetProperty("maxRecordCount").GetInt32();

            if (await AboutServiceAsync(
                    name,
                    $"/rest/services/{name}/FeatureServer/0/query"
                    + $"?resultRecordCount={advertised + 1000}") is not { } query)
            {
                continue;
            }

            int returned = query.GetProperty("features").GetArrayLength();

            Assert.True(
                returned <= advertised,
                $"'{name}' advertises maxRecordCount {advertised} and returned {returned} "
                + $"for a request of {advertised + 1000}.");
        }
    }

    [Fact]
    public async Task A_polygon_ring_is_closed_and_wound_the_way_ArcGIS_requires()
    {
        // ArcGIS reads part structure out of winding: a clockwise ring is a
        // shell, counter-clockwise is a hole. Getting it backwards renders holes
        // as solid and shells as holes, and no error is raised anywhere.
        //
        // <b>Every polygon layer, because winding is a property of the data as much as of
        // the writer.</b> A dataset exported by one tool chain arrives wound the OGC way
        // and another arrives the specification's way — that is exactly what the shapefile
        // reader's containment rule exists for — so a check on one layer says nothing about
        // the next import. Asking one was the coverage habit that let D-21's paging defect
        // live for four days.
        int polygonal = 0;

        foreach ((string name, JsonElement layer, JsonElement query) in
            await EveryLayerAndQueryAsync())
        {
            if (layer.GetProperty("geometryType").GetString() != "esriGeometryPolygon")
            {
                continue;
            }

            JsonElement rings = query.GetProperty("features")[0]
                .GetProperty("geometry").GetProperty("rings");

            Assert.True(rings.GetArrayLength() > 0, $"'{name}' returned a polygon with no rings.");

            JsonElement shell = rings[0];

            Assert.True(
                shell.GetArrayLength() >= 4,
                $"'{name}' returned a ring of {shell.GetArrayLength()} positions; a ring needs at "
                + "least four.");

            double[] first = [shell[0][0].GetDouble(), shell[0][1].GetDouble()];
            JsonElement lastPoint = shell[shell.GetArrayLength() - 1];
            double[] last = [lastPoint[0].GetDouble(), lastPoint[1].GetDouble()];

            Assert.Equal(first[0], last[0]);
            Assert.Equal(first[1], last[1]);

            Assert.True(
                SignedArea(shell) < 0,
                $"'{name}' returned a counter-clockwise outer ring. ArcGIS reads that as a hole, "
                + "so the feature renders inside-out and nothing reports an error.");

            polygonal++;
        }

        Assert.True(
            polygonal > 0,
            "No polygon layer on this server returned a feature, so winding was not checked at "
            + "all. Publish one, or read this as an untested claim rather than a passing test.");
    }

    [Fact]
    public async Task Requesting_a_named_field_returns_that_field_and_the_object_id()
    {
        // outFields is how a client keeps a large layer usable. Dropping the
        // object id from a narrowed response would break selection while looking
        // like it worked.
        string name = await FirstServiceNameAsync(
            "outFields is parsed and applied by one code path for every layer, so this is a question about that path rather than about a layer's fields");

        JsonElement layer = await GetJsonAsync($"/rest/services/{name}/FeatureServer/0");
        string oid = layer.GetProperty("objectIdField").GetString()!;

        string? other = layer.GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("name").GetString()!)
            .FirstOrDefault(n => !string.Equals(n, oid, StringComparison.Ordinal));

        if (other is null)
        {
            return;
        }

        JsonElement query = await GetJsonAsync(
            $"/rest/services/{name}/FeatureServer/0/query?resultRecordCount=1&outFields={other}");

        JsonElement attributes = query.GetProperty("features")[0].GetProperty("attributes");

        Assert.True(attributes.TryGetProperty(other, out _), $"'{other}' was requested and is absent.");
        Assert.True(
            attributes.TryGetProperty(oid, out _),
            "The object id was dropped from a narrowed response, which breaks selection silently.");
    }

    /// <summary>Shoelace. Negative is clockwise in a y-up coordinate system.</summary>
    private static double SignedArea(JsonElement ring)
    {
        double sum = 0;

        for (int i = 0; i < ring.GetArrayLength() - 1; i++)
        {
            double x1 = ring[i][0].GetDouble();
            double y1 = ring[i][1].GetDouble();
            double x2 = ring[i + 1][0].GetDouble();
            double y2 = ring[i + 1][1].GetDouble();

            sum += (x1 * y2) - (x2 * y1);
        }

        return sum / 2;
    }

    /// <summary>
    /// Every layer's document and one page of its features.
    /// </summary>
    /// <remarks>
    /// <b>Two requests per layer, and a layer with no features is skipped rather than
    /// failed.</b> An empty layer is a real thing to publish — a container waiting for an
    /// upload — and it cannot answer a question about the features it does not have. The
    /// caller asserts that at least one layer was examined, which is what stops the whole
    /// check passing vacuously.
    /// </remarks>
    private async Task<IReadOnlyList<(string Name, JsonElement Layer, JsonElement Query)>>
        EveryLayerAndQueryAsync()
    {
        List<(string, JsonElement, JsonElement)> pairs = [];

        foreach (string name in await EveryServiceNameAsync())
        {
            // D-89: skipped only if the catalogue agrees it has gone.
            if (await AboutServiceAsync(name, $"/rest/services/{name}/FeatureServer/0")
                is not { } layer)
            {
                continue;
            }

            if (await AboutServiceAsync(
                    name,
                    $"/rest/services/{name}/FeatureServer/0/query"
                    + "?where=1%3D1&outFields=*&returnGeometry=true&resultRecordCount=1")
                is not { } query)
            {
                continue;
            }

            if (query.GetProperty("features").GetArrayLength() > 0)
            {
                pairs.Add((name, layer, query));
            }
        }

        return pairs;
    }

    private async Task<(JsonElement Layer, JsonElement Query)> LayerAndQueryAsync()
    {
        string name = await FirstServiceNameAsync(
            "outFields is parsed and applied by one code path for every layer, so this is a question about that path rather than about a layer's fields");

        JsonElement layer = await GetJsonAsync($"/rest/services/{name}/FeatureServer/0");
        JsonElement query = await GetJsonAsync(
            $"/rest/services/{name}/FeatureServer/0/query?resultRecordCount=1");

        Assert.True(
            query.GetProperty("features").GetArrayLength() > 0,
            $"'{name}' returned no features, so nothing can be compared against its metadata.");

        return (layer, query);
    }

    /// <summary>
    /// Some FeatureServer a client could add, for a claim that one service can settle.
    /// </summary>
    /// <param name="whyOneIsEnough">
    /// What makes this test's question a question about the server rather than about a layer.
    /// </param>
    /// <returns>The name of a service, folder-qualified if it is in one.</returns>
    /// <remarks>
    /// <para>
    /// <b>The reason is a parameter because [D-65](../../docs/architecture-debt.md) is about
    /// not being able to tell two things apart.</b> Most of this suite asks its question of one
    /// service and is right to: a form's parameters, a button, an `Accept` header are facts
    /// about the server. Some claims are universal — *for every layer this server serves* — and
    /// those must walk. **Nothing at the call site distinguished them**, so a universal claim
    /// asking one service looked exactly like a per-server claim asking one service, and one of
    /// them sat in the suite passing for four days while three of the owner's ten layers were
    /// skipping rows.
    /// </para>
    /// <para>
    /// <b>A parameter rather than a comment, because a comment is optional.</b> The compiler
    /// makes the next person state which kind their test is before it will build, which is the
    /// note D-65 asked for in the only form that cannot be skipped. The string is not read by
    /// anything: its reader is whoever is deciding whether to widen the test.
    /// </para>
    /// </remarks>
    private async Task<string> FirstServiceNameAsync(string whyOneIsEnough)
    {
        Assert.False(
            string.IsNullOrWhiteSpace(whyOneIsEnough),
            "A test that asks one service has to say why one is enough. D-65.");

        string? name = await AnyServiceNameAsync();

        Assert.False(
            string.IsNullOrWhiteSpace(name),
            "No FeatureServer is visible anonymously, at the root or in any folder; this suite "
            + "needs one publicly shared layer.");

        return name!;
    }

    // ---------- paging ----------

    /// <summary>
    /// Pages do not overlap and do not skip, which is what the claim means.
    /// </summary>
    /// <remarks>
    /// <b>The layer document now says <c>supportsPagination: true</c>, and this
    /// is the assertion behind it.</b> Esri's documentation requires a
    /// paginated query with a constant where clause to keep a consistent sort
    /// order across pages; PostgreSQL's LIMIT/OFFSET without an ORDER BY does
    /// not, and page two can repeat rows from page one. If the provider ever
    /// stops ordering by identity when an offset is given, this test is the only
    /// thing that notices — the responses stay well-formed and merely wrong.
    /// </remarks>
    [Fact]
    public async Task Pages_do_not_overlap_or_skip()
    {
        // <b>Every service, not the first — [D-65](../../docs/architecture-debt.md), and this
        // test is the reason that row exists.</b> It sat in the suite passing for the four days
        // that three of the owner's ten layers were skipping rows, because it asked its question
        // of whichever service the catalogue listed first, and that one was small enough for heap
        // order to be identity order. It went red the moment an unrelated change moved which
        // service came first. **D-65's own resolution claimed this test had been widened with
        // three others and it had not** — measured 2026-08-24, it was still taking the first
        // service — so the register recorded the intention and the file kept the habit.
        int examined = 0;

        foreach (string name in await EveryServiceNameAsync())
        {
            JsonElement first = await GetJsonAsync(
                $"/rest/services/{name}/FeatureServer/0/query"
                + "?where=1%3D1&outFields=*&returnGeometry=false&resultRecordCount=2&resultOffset=0");

            if (first.GetProperty("features").GetArrayLength() < 2)
            {
                // Fewer than two features, so there is no second page to compare.
                // A fact about the fixture, not a failure.
                continue;
            }

            JsonElement second = await GetJsonAsync(
                $"/rest/services/{name}/FeatureServer/0/query"
                + "?where=1%3D1&outFields=*&returnGeometry=false&resultRecordCount=2&resultOffset=1");

            string oid = first.GetProperty("objectIdFieldName").GetString()!;

            int[] page1 =
            [
                .. first.GetProperty("features").EnumerateArray()
                    .Select(f => f.GetProperty("attributes").GetProperty(oid).GetInt32()),
            ];

            int[] page2 =
            [
                .. second.GetProperty("features").EnumerateArray()
                    .Select(f => f.GetProperty("attributes").GetProperty(oid).GetInt32()),
            ];

            // Offset one, page size two: the second row of page one must be the
            // first row of page two. Anything else means the order moved between
            // requests, which is the failure pagination without an order produces.
            Assert.Equal(page1[1], page2[0]);

            examined++;
        }

        // <b>A universal claim that examined nothing is a claim about nothing.</b> The same
        // guard the other three widened checks carry, and the reason they carry it: a server
        // whose services all held one feature would pass every assertion above by never
        // reaching one.
        Assert.True(
            examined > 0,
            "No service had two features, so paging was never exercised and this test proved "
            + "nothing. D-65: a test whose coverage is a fact about the data reports on the "
            + "data rather than on the server.");
    }

    /// <summary>
    /// Ordering by a column that is not unique still pages without repeating or skipping.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[D-21](../../docs/architecture-debt.md)'s remaining half, and the row never named
    /// it.</b> That row says *a client that pages without ordering still does not* get stable
    /// pages — which stopped being true on 2026-08-18, when the unordered case got an implicit
    /// order by identity. What was left is the case in between: a caller who **does** order,
    /// by a column that is not unique. An order on a non-unique column is not a total order,
    /// rows that tie may come back in any order, and *any order* is allowed to differ between
    /// two statements.
    /// </para>
    /// <para>
    /// <b>Measured before the repair, on `hosted/ci_many` — 600 rows over two `kind`
    /// values.</b> Six pages of ten ordered by `kind` returned **32 distinct rows of 60, with
    /// 28 repeated**: the first page `[19, 25, 16, 1, 22, 7, 4, 13, 10, 28]` and the second
    /// `[43, 13, 7, 49, 16, 4, 25, 19, 10, 58]`. Every page was individually correct and the
    /// walk was nearly half wrong.
    /// </para>
    /// <para>
    /// <b>It picks the field itself rather than being told one</b>, because a fixture named in
    /// a test is a fixture that has to keep existing — [D-65](../../docs/architecture-debt.md)
    /// is what happens when a check quietly stops covering what it claims. Any text field with
    /// a repeated value will do, and the test says so when no layer has one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Ordering_by_a_column_that_is_not_unique_still_pages_cleanly()
    {
        const int size = 10;
        const int pages = 6;

        int examined = 0;

        foreach (string name in await EveryServiceNameAsync())
        {
            JsonElement whole = await GetJsonAsync(
                $"/rest/services/{name}/FeatureServer/0/query"
                + "?where=1%3D1&outFields=*&returnGeometry=false&resultRecordCount=600");

            JsonElement[] features = [.. whole.GetProperty("features").EnumerateArray()];

            if (features.Length < size * pages)
            {
                continue;
            }

            string oid = whole.GetProperty("objectIdFieldName").GetString()!;

            // A field whose values repeat, so an order on it leaves ties.
            string? tied = null;

            foreach (JsonProperty candidate in features[0].GetProperty("attributes").EnumerateObject())
            {
                if (string.Equals(candidate.Name, oid, StringComparison.Ordinal))
                {
                    continue;
                }

                int distinct = features
                    .Select(f => f.GetProperty("attributes").GetProperty(candidate.Name).ToString())
                    .Distinct(StringComparer.Ordinal)
                    .Count();

                if (distinct > 1 && distinct < features.Length / 4)
                {
                    tied = candidate.Name;
                    break;
                }
            }

            if (tied is null)
            {
                continue;
            }

            HashSet<int> seen = [];
            int repeated = 0;

            for (int page = 0; page < pages; page++)
            {
                JsonElement answer = await GetJsonAsync(
                    $"/rest/services/{name}/FeatureServer/0/query"
                    + $"?where=1%3D1&outFields=*&returnGeometry=false&orderByFields={tied}"
                    + $"&resultRecordCount={size}&resultOffset={page * size}");

                foreach (JsonElement feature in answer.GetProperty("features").EnumerateArray())
                {
                    if (!seen.Add(feature.GetProperty("attributes").GetProperty(oid).GetInt32()))
                    {
                        repeated++;
                    }
                }
            }

            Assert.True(
                repeated == 0 && seen.Count == size * pages,
                $"Paging {name} by '{tied}', which is not unique, walked {pages} pages of "
                + $"{size} and saw {seen.Count} distinct rows with {repeated} repeated. An "
                + "order on a non-unique column is not a total order, so the rows that tie "
                + "may come back differently between two statements and a client walking "
                + "pages repeats some and never sees others. D-21.");

            examined++;
        }

        Assert.True(
            examined > 0,
            $"No layer had {size * pages} features and a field whose values repeat, so this "
            + "was never exercised and proves nothing. D-65: a test whose coverage is a fact "
            + "about the data reports on the data rather than on the server.");
    }

    [Fact]
    public async Task The_layer_document_does_not_understate_what_the_query_endpoint_does()
    {
        // <b>Under-claiming is quieter than over-claiming and not harmless.</b>
        // A client reading supportsPagination=false does not page — it asks for
        // the whole layer or refuses the large ones — so a false negative here
        // costs exactly the capability it hides.
        string name = await FirstServiceNameAsync(
            "these flags come from FeatureServerMetadataWriter and do not vary by layer, so under-claiming is a property of the writer");

        JsonElement layer = await GetJsonAsync($"/rest/services/{name}/FeatureServer/0");

        Assert.True(
            layer.GetProperty("supportsPagination").GetBoolean(),
            "resultOffset and resultRecordCount are honoured, so declaring otherwise tells every "
            + "client not to page.");

        Assert.True(layer.GetProperty("supportsStatistics").GetBoolean());
        Assert.True(layer.GetProperty("supportsDistinct").GetBoolean());

        JsonElement advanced = Require(
            layer,
            "advancedQueryCapabilities",
            "This is where the ArcGIS specification puts these flags, and a client reading only it "
            + "would conclude the server supports nothing.");

        Assert.True(advanced.GetProperty("supportsPagination").GetBoolean());
        Assert.True(advanced.GetProperty("supportsOrderBy").GetBoolean());

        Assert.True(advanced.GetProperty("supportsStatistics").GetBoolean());
        Assert.True(advanced.GetProperty("supportsDistinct").GetBoolean());

        // And the ones that are false are false, which is the other half.
        Assert.False(advanced.GetProperty("supportsSqlExpression").GetBoolean());

        // <b>`supportsPercentileStatistics` was in the list above until this test was found
        // failing.</b> `PERCENTILE_CONT` and `PERCENTILE_DISC` were implemented on 2026-09-04
        // (ADR-052 §3.11) and the flag became true the same day —
        // `FeatureServerMetadataWriterTests` moved with it and `AdvertisedCapabilityTests` began
        // probing that the query actually answers. This assertion did not move, so it went on
        // insisting the server could not do a thing it had just learned, and the run stayed red
        // for twenty-five pushes with nobody able to tell it from a new break. That a percentile
        // *works* is asserted in `AdvertisedCapabilityTests`; what this line is for is that the
        // document does not understate it.
        Assert.True(advanced.GetProperty("supportsPercentileStatistics").GetBoolean());
    }

    // ---------- the query page ----------

    [Fact]
    public async Task The_query_page_is_a_form_when_nothing_has_been_asked()
    {
        // A bare .../query in a browser is somebody about to build a query, not
        // somebody asking for every feature in the layer.
        string name = await FirstServiceNameAsync(
            "the HTML query page is one template the server serves for every layer");

        string page = await GetHtmlAsync($"/rest/services/{name}/FeatureServer/0/query");

        Assert.Contains("<form", page, StringComparison.Ordinal);
        Assert.Contains("name=\"where\"", page, StringComparison.Ordinal);
        Assert.Contains("Query (GET)", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_query_page_has_every_ArcGIS_parameter_on_it()
    {
        // <b>An administrator who knows Esri's page should not have to re-learn
        // this one.</b> A missing control leaves somebody hunting for a field
        // that is simply not drawn.
        string name = await FirstServiceNameAsync(
            "the form's parameter list is the server's, not a layer's");

        string page = await GetHtmlAsync($"/rest/services/{name}/FeatureServer/0/query");

        foreach (string parameter in (string[])
        [
            "where", "objectIds", "geometry", "geometryType", "inSR", "defaultSR", "spatialRel",
            "distance", "units", "relationParam", "outFields", "returnGeometry",
            // havingClause is deliberately absent — D-41 refused the parameter, so
            // the page no longer offers a field for it.
            "maxAllowableOffset", "geometryPrecision", "outSR", "orderByFields",
            "groupByFieldsForStatistics", "outStatistics", "returnZ", "returnM", "gdbVersion",
            "historicMoment", "returnDistinctValues", "resultOffset", "resultRecordCount",
            "returnExtentOnly", "returnCountOnly", "returnIdsOnly", "sqlFormat", "f",
        ])
        {
            Assert.Contains($"name=\"{parameter}\"", page, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task What_the_server_cannot_honour_is_disabled_with_a_reason()
    {
        // <b>Present and greyed out, not absent.</b> A disabled input is not
        // submitted, so the request is exactly what the enabled controls
        // describe — and the reason beside it answers the question on the spot
        // instead of sending somebody to read an error.
        string name = await FirstServiceNameAsync(
            "which controls are disabled is decided by what this server implements, which is the same answer on every layer");

        string page = await GetHtmlAsync($"/rest/services/{name}/FeatureServer/0/query");

        Assert.Contains("Not supported:", page, StringComparison.Ordinal);

        foreach (string refused in (string[]) ["time", "gdbVersion", "historicMoment", "returnZ"])
        {
            int at = page.IndexOf($"name=\"{refused}\"", StringComparison.Ordinal);

            Assert.True(at > 0, refused);

            // The disabled attribute sits inside the same tag.
            int close = page.IndexOf('>', at);

            Assert.Contains(
                "disabled", page[at..close], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_query_page_renders_results_and_the_json_link_still_works()
    {
        string name = await FirstServiceNameAsync(
            "rendering results and the JSON link are the page's behaviour, not the data's");

        string page = await GetHtmlAsync(
            $"/rest/services/{name}/FeatureServer/0/query?where=1%3D1&outFields=*&f=html");

        Assert.Contains("<h3>Results:</h3>", page, StringComparison.Ordinal);

        // The same query as JSON, which is what the page is a view of.
        JsonElement json = await GetJsonAsync(
            $"/rest/services/{name}/FeatureServer/0/query?where=1%3D1&outFields=*");

        Assert.True(json.TryGetProperty("features", out _));
    }

    [Fact]
    public async Task An_explicit_json_format_beats_a_browser_Accept_header()
    {
        // <b>The case that would break every existing caller.</b> A client
        // sending f=json from something that also advertises text/html — a
        // browser-based SDK, a proxy that rewrites Accept — must still get JSON.
        // If the header ever wins, the query endpoint starts returning HTML to
        // machines and nothing in the JSON suite would catch it, because the
        // JSON suite sends no Accept header at all.
        string name = await FirstServiceNameAsync(
            "content negotiation is one decision made before any layer is looked at");

        Assert.Equal(
            "application/json",
            await MediaTypeForAsync(
                $"/rest/services/{name}/FeatureServer/0/query?where=1%3D1&outFields=*", "json"));
    }

    /// <summary>
    /// The form's own default submission is accepted by the server that drew it.
    /// </summary>
    /// <remarks>
    /// <b>Written after the server refused a request its own page generated.</b>
    /// An HTML form submits every enabled control it has, including the ones
    /// nobody touched — so <c>spatialRel=esriSpatialRelIntersects</c> arrived
    /// with an empty <c>geometry</c> on every submission, and a validation rule
    /// written for hand-built URLs refused it with a 400. Every parameter had
    /// been tested individually and all of them passed; the failure was in the
    /// combination the page itself produces, which is the one combination no
    /// per-parameter test covers.
    /// </remarks>
    [Fact]
    public async Task Pressing_the_query_button_works()
    {
        string name = await FirstServiceNameAsync(
            "the button is the page's, and a page that works for one layer works for all of them or is broken for all of them");
        string path = $"/rest/services/{name}/FeatureServer/0/query";

        string form = await GetHtmlAsync(path);

        List<string> submitted = [];

        // Text inputs, as the browser sends them: name and current value, and
        // disabled ones not at all.
        foreach (Match match in Regex.Matches(
            form, "<input type=\"text\" name=\"([^\"]*)\" value=\"([^\"]*)\"[^>]*>"))
        {
            if (match.Value.Contains("disabled", StringComparison.Ordinal))
            {
                continue;
            }

            submitted.Add($"{match.Groups[1].Value}={Uri.EscapeDataString(match.Groups[2].Value)}");
        }

        // Selects send whichever option is selected, or the first.
        foreach (Match match in Regex.Matches(
            form, "<select name=\"([^\"]*)\">(.*?)</select>", RegexOptions.Singleline))
        {
            Match option = Regex.Match(match.Groups[2].Value, "<option value=\"([^\"]*)\" selected>");

            if (!option.Success)
            {
                option = Regex.Match(match.Groups[2].Value, "<option value=\"([^\"]*)\"");
            }

            submitted.Add(
                $"{match.Groups[1].Value}={Uri.EscapeDataString(option.Groups[1].Value)}");
        }

        // Radios send the checked one.
        foreach (Match match in Regex.Matches(
            form, "<input type=\"radio\" name=\"([^\"]*)\" value=\"([^\"]*)\" checked([^>]*)>"))
        {
            if (match.Groups[3].Value.Contains("disabled", StringComparison.Ordinal))
            {
                continue;
            }

            submitted.Add($"{match.Groups[1].Value}={match.Groups[2].Value}");
        }

        Assert.True(
            submitted.Count > 20,
            $"Only {submitted.Count} controls were found on the form, so this test is not "
            + "submitting what a browser would.");

        string page = await GetHtmlAsync($"{path}?{string.Join("&", submitted)}");

        Assert.Contains("<h3>Results:</h3>", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// Opening the query page does not run a query.
    /// </summary>
    /// <remarks>
    /// <b>A link somebody clicks must not be an unfiltered read of the whole
    /// layer</b>, rendered as a table. The Query link on the layer document
    /// carried <c>where=1=1&amp;outFields=*&amp;f=json</c> until 2026-08-15,
    /// which meant clicking it executed exactly that.
    /// </remarks>
    [Fact]
    public async Task The_query_link_opens_a_form_rather_than_running_a_query()
    {
        string name = await FirstServiceNameAsync(
            "where the link goes is a property of the document template");

        string layer = await GetHtmlAsync($"/rest/services/{name}/FeatureServer/0");

        Match link = Regex.Match(layer, "href=\"([^\"]*/query[^\"]*)\"");

        Assert.True(link.Success, "The layer page has no link to the query page.");

        // No query string, so nothing is filtered, ordered or executed.
        Assert.DoesNotContain("?", link.Groups[1].Value, StringComparison.Ordinal);

        string page = await GetHtmlAsync(link.Groups[1].Value);

        Assert.Contains("<form", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<h3>Results:</h3>", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_form_offers_both_output_formats()
    {
        // A table to read and a document to copy into a client, chosen with the
        // control ArcGIS puts in the same place.
        string name = await FirstServiceNameAsync(
            "the format list is the server's set of writers");

        string form = await GetHtmlAsync($"/rest/services/{name}/FeatureServer/0/query");

        Assert.Contains("<option value=\"html\"", form, StringComparison.Ordinal);
        Assert.Contains("<option value=\"json\"", form, StringComparison.Ordinal);

        Assert.Equal(
            "application/json",
            await MediaTypeForAsync(
                $"/rest/services/{name}/FeatureServer/0/query?where=1%3D1&outFields=*", "json"));
    }
}
