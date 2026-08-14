using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
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

    private async Task<string> FirstServiceNameAsync()
    {
        JsonElement services = (await GetJsonAsync("/rest/services")).GetProperty("services");

        Assert.True(
            services.GetArrayLength() > 0,
            "No services are visible anonymously; this suite needs one publicly shared layer.");

        return services.EnumerateArray().First().GetProperty("name").GetString()!;
    }
}
