using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-054 condition 2, owner decision 2026-10-13: a symbology carried by a publish is read as
/// <c>PUT /admin/layers/{name}/symbology</c> reads one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The second writer the condition warned of.</b> ADR-057 let a publish carry its appearance, and the document
/// went into the catalogue as it arrived while the route beside it refused one that did not parse — so a publish
/// could store a symbology nothing could draw, and the fault surfaced at the first map.
/// </para>
/// <para>
/// <b>Both halves</b>: a document that does not parse refuses the publish with the reader's own sentence and
/// publishes nothing, and one that does is stored and read back through the route.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class APublishedSymbologyIsReadTests : ArcGisClient
{
    [Fact]
    public async Task A_publish_refuses_a_symbology_that_does_not_parse_and_stores_one_that_does()
    {
        await RequireServerAsync();

        string? datastore = await DatastoreIdAsync();
        Assert.False(datastore is null, "No datastore data source is registered.");

        var table = await FreeTableAsync();
        Assert.True(table is not null, "The fixture has no table that nothing publishes.");

        string suffix = Guid.NewGuid().ToString("N")[..8];
        string layer = $"zz_pubsym_{suffix}";

        string Publish(string symbology) => JsonSerializer.Serialize(new
        {
            name = layer,
            dataSourceId = datastore,
            schemaName = table!.Value.Schema,
            tableName = table.Value.Table,
            geometryColumn = table.Value.Geometry,
            geometryType = table.Value.Type,
            identityColumn = table.Value.Identity,
            srid = table.Value.Srid,
            symbology,
        });

        try
        {
            // ---- a document that does not parse: refused with the reader's sentence, nothing published ----
            (int refused, string refusedSaid) = await AdminAsync(
                HttpMethod.Post, "/admin/layers", Publish("""{"type":"CIMNoSuchRenderer"}"""));

            Assert.True(refused == 400, $"A symbology that does not parse was answered {refused}: {refusedSaid}");
            Assert.Contains("symbology:", refusedSaid, StringComparison.Ordinal);

            (int missing, _) = await AdminAsync(HttpMethod.Get, $"/admin/layers/{layer}");
            Assert.True(missing == 404, $"The refused publish left a layer behind ({missing}).");

            // ---- one that does: stored, and read back through the route ----
            (int published, string publishedSaid) = await AdminAsync(
                HttpMethod.Post, "/admin/layers", Publish(SimpleRenderer(table.Value.Type)));

            Assert.True(published is 200 or 201, $"A publish with a valid symbology answered {published}: {publishedSaid}");

            (int read, string stored) = await AdminAsync(HttpMethod.Get, $"/admin/layers/{layer}/symbology");
            Assert.True(read == 200, $"Reading the stored symbology answered {read}: {stored}");
            Assert.Contains("CIMSimpleRenderer", stored, StringComparison.Ordinal);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/layers/{layer}");
            await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{layer}");
        }
    }

    /// <summary>A one-symbol CIM renderer the geometry can use.</summary>
    private static string SimpleRenderer(string geometryType)
    {
        string symbol = geometryType.Contains("Point", StringComparison.OrdinalIgnoreCase)
            ? """{ "type": "CIMPointSymbol", "symbolLayers": [ { "type": "CIMVectorMarker", "size": 6 } ] }"""
            : geometryType.Contains("Line", StringComparison.OrdinalIgnoreCase)
                ? """{ "type": "CIMLineSymbol", "symbolLayers": [ { "type": "CIMSolidStroke", "width": 1, "color": { "type": "CIMRGBColor", "values": [40, 40, 40, 100] } } ] }"""
                : """{ "type": "CIMPolygonSymbol", "symbolLayers": [ { "type": "CIMSolidFill", "color": { "type": "CIMRGBColor", "values": [200, 200, 200, 100] } } ] }""";

        return $$"""{ "type": "CIMSimpleRenderer", "label": "all", "symbol": { "symbol": {{symbol}} } }""";
    }
}
