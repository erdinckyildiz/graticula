using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A table with no geometry is served as ArcGIS serves a standalone table — ADR-213 — and related to a layer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every shape asserted here was read off Esri's sample server first</b> (<c>ServiceRequest/FeatureServer</c>,
/// 2026-10-21): a table in the service's <c>tables</c> array numbered with the layers, a document of
/// <c>type: "Table"</c> with no geometry keys, a query whose spatial filter is ignored and whose answer names no
/// geometry type, and <c>returnExtentOnly</c> refused.
/// </para>
/// <para>
/// <b>Designed through the API rather than made in SQL</b>, so the test needs nothing but the server: a point layer and a
/// table in one service, a relationship between them, and the inspection model a field app reads.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class ATableIsServedAsArcGisServesOneTests : ArcGisClient
{
    [Fact]
    public async Task A_designed_table_is_listed_queried_edited_and_related_as_ArcGIS_serves_one()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string run = Guid.NewGuid().ToString("N")[..8];
        string service = "zz_tables_" + run;
        string sites = "zz_sites_" + run;
        string inspections = "zz_inspections_" + run;
        string? relationship = null;

        (HttpStatusCode madeLayer, string layerDesign) = await RequestAsync(
            HttpMethod.Post, $"{root}/admin/hosted/define", token!,
            JsonSerializer.Serialize(new
            {
                name = sites,
                serviceName = service,
                geometryType = "Point",
                srid = 4326,
                fields = new[] { new { name = "site_code", type = "text", nullable = true, length = 10 } },
            }));

        Assert.True(madeLayer == HttpStatusCode.Created, $"Designing the layer answered {(int)madeLayer}: {layerDesign}");

        try
        {
            (HttpStatusCode madeTable, string tableDesign) = await RequestAsync(
                HttpMethod.Post, $"{root}/admin/hosted/define", token!,
                JsonSerializer.Serialize(new
                {
                    name = inspections,
                    serviceName = service,
                    geometryType = "Table",
                    fields = new object[]
                    {
                        new { name = "site_code", type = "text", nullable = true, length = 10 },
                        new { name = "score", type = "integer", nullable = true },
                    },
                }));

            Assert.True(madeTable == HttpStatusCode.Created, $"Designing the table answered {(int)madeTable}: {tableDesign}");

            JsonElement designed = JsonDocument.Parse(tableDesign).RootElement;
            Assert.Equal("Table", designed.GetProperty("geometryType").GetString());
            Assert.Equal(JsonValueKind.Null, designed.GetProperty("services").GetProperty("tiles").ValueKind);

            string feature = $"/rest/services/hosted/{service}/FeatureServer";

            // ---------- the service lists it apart, in one numbering ----------

            JsonElement document = await GetJsonAsync($"{feature}?f=json");
            JsonElement layer = document.GetProperty("layers").EnumerateArray().Single();
            JsonElement table = document.GetProperty("tables").EnumerateArray().Single();

            Assert.Equal(0, layer.GetProperty("id").GetInt32());
            Assert.Equal(1, table.GetProperty("id").GetInt32());
            Assert.Equal(inspections, table.GetProperty("name").GetString());

            // ---------- its own document is a table's ----------

            JsonElement own = await GetJsonAsync($"{feature}/1?f=json");

            Assert.Equal("Table", own.GetProperty("type").GetString());

            foreach (string key in (string[])["geometryType", "extent", "drawingInfo", "hasZ", "allowGeometryUpdates"])
            {
                Assert.False(own.TryGetProperty(key, out _), $"The table's document carries '{key}', which ArcGIS's does not.");
            }

            // ---------- edited: rows without a shape go in, a row with one is refused ----------

            await EditAsync(root, token!, $"{feature}/0", """[{"attributes":{"site_code":"A1"},"geometry":{"x":32.85,"y":39.93}}]""");
            await EditAsync(root, token!, $"{feature}/1", """[{"attributes":{"site_code":"A1","score":4}},{"attributes":{"site_code":"A1","score":2}},{"attributes":{"site_code":"B2","score":5}}]""");

            (HttpStatusCode _, string shaped) = await RequestAsync(
                HttpMethod.Post,
                $"{root}{feature}/1/applyEdits?f=json&adds="
                + Uri.EscapeDataString("""[{"attributes":{"site_code":"C3"},"geometry":{"x":1,"y":2}}]"""),
                token!, json: null);

            JsonElement refused = JsonDocument.Parse(shaped).RootElement.GetProperty("addResults")[0];
            Assert.False(refused.GetProperty("success").GetBoolean(), $"A row with a geometry was stored in a table: {shaped}");

            // ---------- queried: a spatial filter is ignored and the answer names no geometry ----------

            JsonElement count = await GetJsonAsync(
                $"{feature}/1/query?where=1%3D1&geometry=0,0,1,1&geometryType=esriGeometryEnvelope&returnCountOnly=true&f=json");
            Assert.Equal(3, count.GetProperty("count").GetInt32());

            JsonElement rows = await GetJsonAsync($"{feature}/1/query?where=score%3E2&outFields=*&returnGeometry=true&f=json");
            Assert.False(rows.TryGetProperty("geometryType", out _));
            Assert.False(rows.TryGetProperty("spatialReference", out _));
            Assert.Equal(2, rows.GetProperty("features").GetArrayLength());
            Assert.All(rows.GetProperty("features").EnumerateArray(), row => Assert.False(row.TryGetProperty("geometry", out _)));

            (HttpStatusCode extent, string why) = await RequestAsync(
                HttpMethod.Get, $"{root}{feature}/1/query?where=1%3D1&returnExtentOnly=true&f=json", token!, json: null);
            Assert.True(extent == HttpStatusCode.BadRequest || why.Contains("\"code\":400", StringComparison.Ordinal),
                $"returnExtentOnly on a table answered {(int)extent}: {why}");

            // ---------- related: the site's inspections, through the relationship both documents name ----------

            (HttpStatusCode declared, string answer) = await RequestAsync(
                HttpMethod.Post, $"{root}/admin/relationships", token!,
                JsonSerializer.Serialize(new
                {
                    name = "zz_site_inspections_" + run,
                    originLayer = sites,
                    originKey = "site_code",
                    relatedLayer = inspections,
                    relatedKey = "site_code",
                    cardinality = "OneToMany",
                    composite = false,
                }));

            Assert.True(declared is HttpStatusCode.Created or HttpStatusCode.OK, $"Declaring the relationship answered {(int)declared}: {answer}");
            relationship = JsonDocument.Parse(answer).RootElement.GetProperty("id").GetString();

            JsonElement fromTable = (await GetJsonAsync($"{feature}/1?f=json")).GetProperty("relationships")[0];
            Assert.Equal(0, fromTable.GetProperty("relatedTableId").GetInt32());

            int number = (await GetJsonAsync($"{feature}/0?f=json")).GetProperty("relationships")[0].GetProperty("id").GetInt32();
            JsonElement related = await GetJsonAsync(
                $"{feature}/0/queryRelatedRecords?objectIds=1&relationshipId={number}&outFields=*&f=json");

            Assert.Equal(2, related.GetProperty("relatedRecordGroups")[0].GetProperty("relatedRecords").GetArrayLength());

            // ---------- the map service lists it and answers it as a table, and draws nothing of it ----------

            string map = $"/rest/services/hosted/{service}/MapServer";
            JsonElement mapped = await GetJsonAsync($"{map}?f=json");

            Assert.Equal(1, mapped.GetProperty("tables").EnumerateArray().Single().GetProperty("id").GetInt32());
            Assert.Equal("Table", (await GetJsonAsync($"{map}/1?f=json")).GetProperty("type").GetString());

            // ---------- OGC API Features: a collection with no spatial extent, whose features have no geometry ----------

            JsonElement collection = await GetJsonAsync($"/ogc/features/v1/collections/{inspections}?f=json");
            Assert.False(collection.GetProperty("extent").TryGetProperty("spatial", out _),
                "A table's collection claims a spatial extent.");

            JsonElement items = await GetJsonAsync($"/ogc/features/v1/collections/{inspections}/items?f=json");
            Assert.Equal(3, items.GetProperty("numberMatched").GetInt32());
            Assert.All(items.GetProperty("features").EnumerateArray(),
                f => Assert.Equal(JsonValueKind.Null, f.GetProperty("geometry").ValueKind));

            // A feature with no geometry intersects no box: the rule OgcExtentConformanceTests holds this face to.
            JsonElement boxed = await GetJsonAsync(
                $"/ogc/features/v1/collections/{inspections}/items?f=json&bbox=-180,-90,180,90");
            Assert.Equal(0, boxed.GetProperty("numberMatched").GetInt32());

            // ---------- and the services listing counts it as a table, not a layer ----------

            JsonElement listed = (await GetJsonAsync("/admin/featureservices")).GetProperty("services").EnumerateArray()
                .Single(s => s.GetProperty("name").GetString() == service);
            Assert.Equal(1, listed.GetProperty("layers").GetInt32());
            Assert.Equal(1, listed.GetProperty("tables").GetInt32());
        }
        finally
        {
            if (relationship is not null)
            {
                await RequestAsync(HttpMethod.Delete, $"{root}/admin/relationships/{relationship}", token!, json: null);
            }

            await RequestAsync(
                HttpMethod.Delete, $"{root}/admin/featureservices/{service}?folder=hosted&drop=true", token!, json: null);
        }
    }

    private async Task EditAsync(string root, string token, string layer, string adds)
    {
        (HttpStatusCode status, string body) = await RequestAsync(
            HttpMethod.Post, $"{root}{layer}/applyEdits?f=json&adds={Uri.EscapeDataString(adds)}", token, json: null);

        Assert.True(status == HttpStatusCode.OK, $"applyEdits on {layer} answered {(int)status}: {body}");
        Assert.All(
            JsonDocument.Parse(body).RootElement.GetProperty("addResults").EnumerateArray(),
            added => Assert.True(added.GetProperty("success").GetBoolean(), body));
    }
}
