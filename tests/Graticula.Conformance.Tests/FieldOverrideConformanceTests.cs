using System;
using System.Collections.Generic;
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
/// ADR-063: a layer may label a column and may hide one, and hidden means hidden everywhere.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every assertion is against a layer this class imports and deletes.</b> Hiding a column on
/// a shared fixture layer would change what every other class sees while it ran — the shape
/// [D-197](../../docs/architecture-debt.md) found a class counting features in a layer another
/// class was writing to — so it imports its own and joins the collection that serialises
/// catalogue changes.
/// </para>
/// <para>
/// <b>The test of hiding is a refusal, not a document — condition 1.</b> A column absent from the
/// layer document and usable in a <c>where</c> clause is not hidden; it is searchable one
/// predicate at a time, which is how a value is recovered without ever being displayed. So each
/// door is asked about the hidden column and about a column that has never existed, and the two
/// answers must be the same answer — a caller must not be able to tell them apart.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class FieldOverrideConformanceTests : ArcGisClient
{
    private const string Hidden = "secret";

    // Never a column of the imported table. The two answers are compared by substituting one
    // name for the other in the hidden column's answer.
    private const string Absent = "nosuch";

    [Fact]
    public async Task A_hidden_column_is_refused_exactly_as_an_absent_one_at_every_door()
    {
        (string root, string token, string name) = await ImportAsync();

        try
        {
            await PutOverridesAsync(
                root, token, name,
                $"[{{\"column\":\"{Hidden}\",\"hidden\":true}},"
                + "{\"column\":\"pop\",\"alias\":\"Population\"}]",
                HttpStatusCode.OK);

            string layer = $"/rest/services/hosted/{name}/FeatureServer/0";

            // ------------------------------------------------------------ the document
            JsonElement document = await GetJsonAsync($"{layer}?f=json");
            JsonElement[] fields = [.. document.GetProperty("fields").EnumerateArray()];

            Assert.DoesNotContain(fields, f => f.GetProperty("name").GetString() == Hidden);

            JsonElement pop = Assert.Single(fields, f => f.GetProperty("name").GetString() == "pop");
            Assert.Equal("Population", pop.GetProperty("alias").GetString());

            // An unlabelled column is labelled with its own name, not with nothing.
            JsonElement label = Assert.Single(
                fields, f => f.GetProperty("name").GetString() == "label");
            Assert.Equal("label", label.GetProperty("alias").GetString());

            // ------------------------------------------------------------ outFields=* carries no value
            JsonElement all = await GetJsonAsync($"{layer}/query?where=1%3D1&outFields=*&f=json");

            foreach (JsonElement feature in all.GetProperty("features").EnumerateArray())
            {
                Assert.False(
                    feature.GetProperty("attributes").TryGetProperty(Hidden, out _),
                    $"outFields=* returned the hidden column: {feature}");
            }

            // ------------------------------------------------------------ every door, both names
            string[] doors =
            [
                $"{layer}/query?where=1%3D1&outFields=@&f=json",
                $"{layer}/query?where=@%3D%27a%27&outFields=objectid&f=json",
                $"{layer}/query?where=1%3D1&outFields=objectid&orderByFields=@&f=json",
                $"{layer}/query?where=1%3D1&f=json&outStatistics="
                    + Uri.EscapeDataString(
                        "[{\"statisticType\":\"count\",\"onStatisticField\":\"@\","
                        + "\"outStatisticFieldName\":\"n\"}]"),
                $"{layer}/query?where=1%3D1&f=json&groupByFieldsForStatistics=@&outStatistics="
                    + Uri.EscapeDataString(
                        "[{\"statisticType\":\"count\",\"onStatisticField\":\"objectid\","
                        + "\"outStatisticFieldName\":\"n\"}]"),
            ];

            foreach (string door in doors)
            {
                (int hiddenStatus, string hiddenBody) =
                    await AnswerAsync(root, token, door.Replace("@", Hidden, StringComparison.Ordinal));
                (int absentStatus, string absentBody) =
                    await AnswerAsync(root, token, door.Replace("@", Absent, StringComparison.Ordinal));

                Assert.True(
                    absentStatus >= 400 || absentBody.Contains("\"error\"", StringComparison.Ordinal),
                    $"The door does not refuse a column that has never existed, so it cannot be "
                    + $"used to test hiding: {door} answered {absentStatus} {absentBody}");

                Assert.Equal(absentStatus, hiddenStatus);
                Assert.Equal(absentBody, hiddenBody.Replace(Hidden, Absent, StringComparison.Ordinal));
            }

            // ------------------------------------------------------------ and the write door
            long objectId = all.GetProperty("features").EnumerateArray().First()
                .GetProperty("attributes").GetProperty("objectid").GetInt64();

            string hiddenEdit = await UpdateAsync(root, token, name, objectId, Hidden);
            string absentEdit = await UpdateAsync(root, token, name, objectId, Absent);

            Assert.Contains("\"success\":false", absentEdit.Replace(" ", "", StringComparison.Ordinal),
                StringComparison.Ordinal);
            Assert.Equal(absentEdit, hiddenEdit.Replace(Hidden, Absent, StringComparison.Ordinal));

            // ------------------------------------------------------------ the OGC face carries no value
            // Found by name in the listing rather than assembled, so this asserts the face and
            // not this test's idea of how the face spells a collection.
            JsonElement collections = await GetJsonAsync("/ogc/features/v1/collections");
            string collection = collections.GetProperty("collections").EnumerateArray()
                .Select(c => c.GetProperty("id").GetString() ?? string.Empty)
                .Single(id => id.Contains(name, StringComparison.Ordinal));

            JsonElement items = await GetJsonAsync(
                $"/ogc/features/v1/collections/{Uri.EscapeDataString(collection)}/items?limit=5");

            foreach (JsonElement feature in items.GetProperty("features").EnumerateArray())
            {
                Assert.False(
                    feature.GetProperty("properties").TryGetProperty(Hidden, out _),
                    $"The OGC face returned the hidden column: {feature}");
            }

            // ------------------------------------------------------------ and it comes back
            // Unhiding restores the column, which is the evidence that the value was never
            // removed from the table — the override is a statement about the layer.
            await PutOverridesAsync(root, token, name, "[]", HttpStatusCode.OK);

            JsonElement restored = await GetJsonAsync($"{layer}?f=json");

            Assert.Contains(
                restored.GetProperty("fields").EnumerateArray(),
                f => f.GetProperty("name").GetString() == Hidden);
        }
        finally
        {
            await DeleteAsync(root, token, name);
        }
    }

    [Theory]
    [InlineData("objectid", "cannot be hidden")]
    [InlineData("geom", "geometry column")]
    public async Task The_columns_a_layer_cannot_be_a_layer_without_are_refused_when_hidden(
        string column, string because)
    {
        (string root, string token, string name) = await ImportAsync();

        try
        {
            string body = await PutOverridesAsync(
                root, token, name,
                $"[{{\"column\":\"{column}\",\"hidden\":true}}]",
                HttpStatusCode.BadRequest);

            Assert.Contains(because, body, StringComparison.Ordinal);

            // <b>Refused at write, condition 2 — so nothing was stored.</b> A check at query time
            // is a check somebody can reach a state without passing.
            JsonElement document = await GetJsonAsync(
                $"/rest/services/hosted/{name}/FeatureServer/0?f=json");

            Assert.Equal("objectid", document.GetProperty("objectIdField").GetString());
        }
        finally
        {
            await DeleteAsync(root, token, name);
        }
    }

    [Fact]
    public async Task An_override_naming_a_column_the_table_does_not_have_is_kept_and_reported()
    {
        (string root, string token, string name) = await ImportAsync();

        try
        {
            string body = await PutOverridesAsync(
                root, token, name,
                "[{\"column\":\"renamed_away\",\"alias\":\"Old label\"},"
                + "{\"column\":\"pop\",\"alias\":\"Population\"}]",
                HttpStatusCode.OK);

            JsonElement answer = JsonDocument.Parse(body).RootElement;
            JsonElement inert = Assert.Single(answer.GetProperty("inert").EnumerateArray());

            Assert.Equal("renamed_away", inert.GetProperty("column").GetString());

            // Reported on the read as well as on the write: an operator who lost a label finds
            // out why by looking, not only at the moment they caused it.
            (int status, string read) = await AnswerAsync(
                root, token, $"/admin/layers/{name}/fields");

            Assert.Equal(200, status);
            Assert.Contains("renamed_away", read, StringComparison.Ordinal);

            // And it is inert: the layer document is unaffected by it.
            JsonElement document = await GetJsonAsync(
                $"/rest/services/hosted/{name}/FeatureServer/0?f=json");

            Assert.DoesNotContain(
                document.GetProperty("fields").EnumerateArray(),
                f => f.GetProperty("name").GetString() == "renamed_away");
        }
        finally
        {
            await DeleteAsync(root, token, name);
        }
    }

    // ------------------------------------------------------------------------------------------

    private async Task<(string Root, string Token, string Name)> ImportAsync()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string name = $"zz_fo_{Guid.NewGuid():N}"[..18];

        // Three properties the test controls the names of: one to label, one to hide, one left
        // alone. Lower case, so the importer's own name handling is not what is being tested.
        const string GeoJson = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","geometry":{"type":"Point","coordinates":[29.0,41.0]},
               "properties":{"label":"a","secret":"s1","pop":10}},
              {"type":"Feature","geometry":{"type":"Point","coordinates":[29.1,41.1]},
               "properties":{"label":"b","secret":"s2","pop":20}}
            ]}
            """;

        using MultipartFormDataContent form = new();
        using ByteArrayContent bytes = new(Encoding.UTF8.GetBytes(GeoJson));

        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/geo+json");
        form.Add(bytes, "file", "fields.geojson");
        form.Add(new StringContent(name), "name");

        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}/admin/hosted/import")
        {
            Content = form,
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.IsSuccessStatusCode,
            $"The import of '{name}' failed with {(int)response.StatusCode}: {body}");

        return (root, token!, name);
    }

    private async Task<string> PutOverridesAsync(
        string root, string token, string name, string overrides, HttpStatusCode expected)
    {
        (HttpStatusCode status, string body) = await RequestAsync(
            HttpMethod.Put, $"{root}/admin/layers/{name}/fields", token,
            $"{{\"overrides\":{overrides}}}");

        Assert.True(status == expected, $"PUT fields answered {(int)status}, expected {(int)expected}: {body}");

        return body;
    }

    private async Task<(int Status, string Body)> AnswerAsync(string root, string token, string path)
    {
        (HttpStatusCode status, string body) = await RequestAsync(
            HttpMethod.Get, $"{root}{path}", token, json: null);

        return ((int)status, body);
    }

    private async Task<string> UpdateAsync(
        string root, string token, string name, long objectId, string column)
    {
        using FormUrlEncodedContent content = new(
        [
            new KeyValuePair<string, string>(
                "features",
                $"[{{\"attributes\":{{\"objectid\":{objectId},\"{column}\":\"x\"}}}}]"),
            new KeyValuePair<string, string>("f", "json"),
        ]);

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            $"{root}/rest/services/hosted/{name}/FeatureServer/0/updateFeatures")
        {
            Content = content,
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return await response.Content.ReadAsStringAsync();
    }

    private async Task DeleteAsync(string root, string token, string name)
    {
        // The table this class made, and only that — `drop=true` on a hosted service it imported
        // a moment ago, which is what `HostedDeleteTests` does with its own.
        await RequestAsync(
            HttpMethod.Delete, $"{root}/admin/featureservices/{name}?folder=hosted&drop=true",
            token, json: null);
    }
}
