using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace GisServer.Conformance.Tests;

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
public sealed class ArcGisConsistencyTests : ArcGisClient
{
    [Fact]
    public async Task The_query_names_the_same_object_id_field_the_layer_document_declares()
    {
        (JsonElement layer, JsonElement query) = await LayerAndQueryAsync();

        // A client selects and pages by this name. If they differ it matches
        // nothing, silently, forever.
        Assert.Equal(
            layer.GetProperty("objectIdField").GetString(),
            query.GetProperty("objectIdFieldName").GetString());
    }

    [Fact]
    public async Task The_query_returns_the_geometry_type_the_layer_document_promised()
    {
        (JsonElement layer, JsonElement query) = await LayerAndQueryAsync();

        // The client has already built a polygon renderer by the time the first
        // feature arrives.
        Assert.Equal(
            layer.GetProperty("geometryType").GetString(),
            query.GetProperty("geometryType").GetString());
    }

    [Fact]
    public async Task The_query_returns_the_spatial_reference_the_layer_document_promised()
    {
        (JsonElement layer, JsonElement query) = await LayerAndQueryAsync();

        int declared = layer.GetProperty("extent").ValueKind == JsonValueKind.Null
            ? query.GetProperty("spatialReference").GetProperty("wkid").GetInt32()
            : layer.GetProperty("extent").GetProperty("spatialReference").GetProperty("wkid").GetInt32();

        Assert.Equal(declared, query.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
    }

    [Fact]
    public async Task The_object_id_is_emitted_as_a_number_because_it_is_declared_as_an_OID()
    {
        // The bug this pins, found by reading a real response: objectid came out
        // as "1" in quotes while the field was declared esriFieldTypeOID. A
        // client paging or selecting against a quoted value matches nothing and
        // is told nothing.
        (JsonElement layer, JsonElement query) = await LayerAndQueryAsync();

        string oid = layer.GetProperty("objectIdField").GetString()!;
        JsonElement attributes = query.GetProperty("features")[0].GetProperty("attributes");

        Assert.True(
            attributes.TryGetProperty(oid, out JsonElement value),
            $"The features do not carry '{oid}', which objectIdFieldName names. A client cannot "
            + "page or select against a field that is not in the response.");

        Assert.Equal(JsonValueKind.Number, value.ValueKind);
    }

    [Fact]
    public async Task Every_attribute_returned_was_declared_as_a_field()
    {
        // An undeclared attribute has no column in the client's table, so the
        // value is fetched, transferred, and dropped.
        (JsonElement layer, JsonElement query) = await LayerAndQueryAsync();

        HashSet<string> declared = [.. layer.GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("name").GetString()!)];

        foreach (JsonProperty attribute in query.GetProperty("features")[0]
            .GetProperty("attributes").EnumerateObject())
        {
            Assert.True(
                declared.Contains(attribute.Name),
                $"'{attribute.Name}' came back in a feature and is not in the layer's field list.");
        }
    }

    [Fact]
    public async Task Every_declared_field_type_matches_the_value_actually_sent()
    {
        // The silent class of failure: a field declared as an integer and sent
        // as a string parses back as a number and loses precision; declared as a
        // number and sent as a string sorts as text. Neither raises anything.
        (JsonElement layer, JsonElement query) = await LayerAndQueryAsync();

        Dictionary<string, string> declared = layer.GetProperty("fields").EnumerateArray()
            .ToDictionary(
                f => f.GetProperty("name").GetString()!,
                f => f.GetProperty("type").GetString()!);

        foreach (JsonProperty attribute in query.GetProperty("features")[0]
            .GetProperty("attributes").EnumerateObject())
        {
            if (attribute.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

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
                $"'{attribute.Name}' is declared {type} and was sent as {kind}. A client parses "
                + "the value according to the declaration, so this is wrong in a way nothing "
                + "reports.");
        }
    }

    [Fact]
    public async Task The_advertised_maximum_is_not_exceeded_by_a_request_for_more()
    {
        // A client that respects maxRecordCount never triggers a server-side
        // clamp. One that ignores it must still not be able to ask for more than
        // the server said it would give.
        string name = await FirstServiceNameAsync();

        JsonElement layer = await GetJsonAsync($"/rest/services/{name}/FeatureServer/0");
        int advertised = layer.GetProperty("maxRecordCount").GetInt32();

        JsonElement query = await GetJsonAsync(
            $"/rest/services/{name}/FeatureServer/0/query?resultRecordCount={advertised + 1000}");

        Assert.True(query.GetProperty("features").GetArrayLength() <= advertised);
    }

    [Fact]
    public async Task A_polygon_ring_is_closed_and_wound_the_way_ArcGIS_requires()
    {
        // ArcGIS reads part structure out of winding: a clockwise ring is a
        // shell, counter-clockwise is a hole. Getting it backwards renders holes
        // as solid and shells as holes, and no error is raised anywhere.
        (JsonElement layer, JsonElement query) = await LayerAndQueryAsync();

        if (layer.GetProperty("geometryType").GetString() != "esriGeometryPolygon")
        {
            return;
        }

        JsonElement rings = query.GetProperty("features")[0]
            .GetProperty("geometry").GetProperty("rings");

        Assert.True(rings.GetArrayLength() > 0);

        JsonElement shell = rings[0];
        Assert.True(shell.GetArrayLength() >= 4, "A ring needs at least four positions.");

        double[] first = [shell[0][0].GetDouble(), shell[0][1].GetDouble()];
        JsonElement lastPoint = shell[shell.GetArrayLength() - 1];
        double[] last = [lastPoint[0].GetDouble(), lastPoint[1].GetDouble()];

        Assert.Equal(first[0], last[0]);
        Assert.Equal(first[1], last[1]);

        Assert.True(
            SignedArea(shell) < 0,
            "The outer ring is counter-clockwise. ArcGIS reads that as a hole, so the feature "
            + "renders inside-out and nothing reports an error.");
    }

    [Fact]
    public async Task Requesting_a_named_field_returns_that_field_and_the_object_id()
    {
        // outFields is how a client keeps a large layer usable. Dropping the
        // object id from a narrowed response would break selection while looking
        // like it worked.
        string name = await FirstServiceNameAsync();

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

    private async Task<(JsonElement Layer, JsonElement Query)> LayerAndQueryAsync()
    {
        string name = await FirstServiceNameAsync();

        JsonElement layer = await GetJsonAsync($"/rest/services/{name}/FeatureServer/0");
        JsonElement query = await GetJsonAsync(
            $"/rest/services/{name}/FeatureServer/0/query?resultRecordCount=1");

        Assert.True(
            query.GetProperty("features").GetArrayLength() > 0,
            $"'{name}' returned no features, so nothing can be compared against its metadata.");

        return (layer, query);
    }

    /// <summary>Some FeatureServer a client could add, folders included.</summary>
    /// <remarks>
    /// <b>Folders included, because every hosted layer lands in one.</b> This
    /// read only the root array and failed with "no services are visible
    /// anonymously" against a server published entirely through the hosting API
    /// -- which is every server CI builds from nothing.
    /// </remarks>
    private async Task<string> FirstServiceNameAsync()
    {
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
        string name = await FirstServiceNameAsync();

        JsonElement first = await GetJsonAsync(
            $"/rest/services/{name}/FeatureServer/0/query"
            + "?where=1%3D1&outFields=*&returnGeometry=false&resultRecordCount=2&resultOffset=0");

        if (first.GetProperty("features").GetArrayLength() < 2)
        {
            // Fewer than two features, so there is no second page to compare.
            // A fact about the fixture, not a failure.
            return;
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
    }

    [Fact]
    public async Task The_layer_document_does_not_understate_what_the_query_endpoint_does()
    {
        // <b>Under-claiming is quieter than over-claiming and not harmless.</b>
        // A client reading supportsPagination=false does not page — it asks for
        // the whole layer or refuses the large ones — so a false negative here
        // costs exactly the capability it hides.
        string name = await FirstServiceNameAsync();

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
        Assert.False(advanced.GetProperty("supportsPercentileStatistics").GetBoolean());
    }

    // ---------- the query page ----------

    [Fact]
    public async Task The_query_page_is_a_form_when_nothing_has_been_asked()
    {
        // A bare .../query in a browser is somebody about to build a query, not
        // somebody asking for every feature in the layer.
        string name = await FirstServiceNameAsync();

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
        string name = await FirstServiceNameAsync();

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
        string name = await FirstServiceNameAsync();

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
        string name = await FirstServiceNameAsync();

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
        string name = await FirstServiceNameAsync();

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
        string name = await FirstServiceNameAsync();
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
        string name = await FirstServiceNameAsync();

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
        string name = await FirstServiceNameAsync();

        string form = await GetHtmlAsync($"/rest/services/{name}/FeatureServer/0/query");

        Assert.Contains("<option value=\"html\"", form, StringComparison.Ordinal);
        Assert.Contains("<option value=\"json\"", form, StringComparison.Ordinal);

        Assert.Equal(
            "application/json",
            await MediaTypeForAsync(
                $"/rest/services/{name}/FeatureServer/0/query?where=1%3D1&outFields=*", "json"));
    }
}
