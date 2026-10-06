using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-169: WFS-T — a feature inserted, updated, replaced and deleted through <c>Transaction</c>, in WFS 2.0.0 and
/// 1.1.0, on the editable layer the OGC write suite uses; and refused to a caller who may not edit.
/// </summary>
[Collection("catalogue walk")]
public sealed class WfsTransactionConformanceTests : ArcGisClient
{
    private const string Wfs20 = "http://www.opengis.net/wfs/2.0";

    private static string Layer()
    {
        string? qualified = Environment.GetEnvironmentVariable(OgcWriteConformanceTests.LayerVariable);
        Assert.False(string.IsNullOrWhiteSpace(qualified),
            $"{OgcWriteConformanceTests.LayerVariable} is not set, so this test FAILS rather than skips.");
        int slash = qualified!.LastIndexOf('/');
        return slash < 0 ? qualified : qualified[(slash + 1)..];
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path, string? xml = null, bool signedIn = true)
    {
        string root = await RequireServerAsync();
        using HttpRequestMessage request = new(method, new Uri($"{root}{path}"));

        if (signedIn)
        {
            await AuthenticateAsync(request, root);
        }

        if (xml is not null)
        {
            request.Content = new StringContent(xml, Encoding.UTF8, "text/xml");
        }

        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string Equal(string ns, string prefix, string property, string value, string reference = "ValueReference") =>
        $"<{prefix}:Filter xmlns:{prefix}=\"{ns}\"><{prefix}:PropertyIsEqualTo><{prefix}:{reference}>{property}</{prefix}:{reference}>"
        + $"<{prefix}:Literal>{value}</{prefix}:Literal></{prefix}:PropertyIsEqualTo></{prefix}:Filter>";

    [Fact]
    public async Task A_feature_is_inserted_updated_replaced_and_deleted_through_transaction()
    {
        string layer = Layer();
        string items = $"/ogc/features/v1/collections/{Uri.EscapeDataString(layer)}/items";

        // The properties are read from the layer, not invented (D-65): a text one, and the geometry's element.
        (_, string page) = await SendAsync(HttpMethod.Get, $"{items}?limit=1");
        string text = JsonDocument.Parse(page).RootElement.GetProperty("features")[0].GetProperty("properties")
            .EnumerateObject().First(p => p.Value.ValueKind == JsonValueKind.String && p.Name != "globalid").Name;
        (_, string schema) = await SendAsync(HttpMethod.Get,
            $"/wfs?service=WFS&version=2.0.0&request=DescribeFeatureType&typeNames=graticula:{layer}");
        string geometry = XDocument.Parse(schema).Descendants()
            .First(e => e.Name.LocalName == "element" && ((string?)e.Attribute("type") ?? "").StartsWith("gml:", StringComparison.Ordinal))
            .Attribute("name")!.Value;

        string probe = $"wfst-{Guid.NewGuid():N}"[..14];
        string inserted = await InsertAsync(layer, text, geometry, probe);
        Assert.StartsWith($"{layer}.", inserted, StringComparison.Ordinal);

        try
        {
            // Read back where it was put: 32.87° E, 39.96° N, given in EPSG:4326's own order.
            (_, string read) = await SendAsync(HttpMethod.Get, $"{items}?{text}={probe}");
            JsonElement coordinates = JsonDocument.Parse(read).RootElement.GetProperty("features")[0]
                .GetProperty("geometry").GetProperty("coordinates");
            Assert.Equal(32.8712, coordinates[0].GetDouble(), 4);
            Assert.Equal(39.9601, coordinates[1].GetDouble(), 4);

            // Update in 1.1.0, by an OGC Filter 1.1 over the text property.
            string renamed = probe + "u";
            (HttpStatusCode updated, string updateBody) = await SendAsync(HttpMethod.Post, "/wfs",
                "<wfs:Transaction service=\"WFS\" version=\"1.1.0\" xmlns:wfs=\"http://www.opengis.net/wfs\" "
                + "xmlns:graticula=\"urn:graticula:ns\">"
                + $"<wfs:Update typeName=\"graticula:{layer}\"><wfs:Property><wfs:Name>{text}</wfs:Name><wfs:Value>{renamed}</wfs:Value></wfs:Property>"
                + Equal("http://www.opengis.net/ogc", "ogc", text, probe, "PropertyName") + "</wfs:Update></wfs:Transaction>");
            Assert.True(updated == HttpStatusCode.OK, updateBody);
            Assert.Equal("1", XDocument.Parse(updateBody).Descendants().First(e => e.Name.LocalName == "totalUpdated").Value);

            // Replace in 2.0.0: the whole feature, somewhere else.
            (HttpStatusCode replaced, string replaceBody) = await SendAsync(HttpMethod.Post, "/wfs",
                $"<wfs:Transaction service=\"WFS\" version=\"2.0.0\" xmlns:wfs=\"{Wfs20}\" xmlns:gml=\"http://www.opengis.net/gml/3.2\" "
                + "xmlns:graticula=\"urn:graticula:ns\"><wfs:Replace>"
                + $"<graticula:{layer}><graticula:{text}>{renamed}</graticula:{text}><graticula:{geometry}>"
                + "<gml:Point srsName=\"urn:ogc:def:crs:EPSG::4326\"><gml:pos>39.97 32.88</gml:pos></gml:Point>"
                + $"</graticula:{geometry}></graticula:{layer}>"
                + Equal("http://www.opengis.net/fes/2.0", "fes", text, renamed) + "</wfs:Replace></wfs:Transaction>");
            Assert.True(replaced == HttpStatusCode.OK, replaceBody);
            Assert.Equal("1", XDocument.Parse(replaceBody).Descendants().First(e => e.Name.LocalName == "totalReplaced").Value);

            // 2.0 names the replaced feature in ReplaceResults (§15.3.6); it keeps its identity.
            Assert.Equal(inserted, XDocument.Parse(replaceBody).Descendants().First(e => e.Name.LocalName == "ReplaceResults")
                .Descendants().First(e => e.Name.LocalName == "ResourceId").Attribute("rid")!.Value);
            (_, string moved) = await SendAsync(HttpMethod.Get, $"{items}?{text}={renamed}");
            Assert.Equal(32.88, JsonDocument.Parse(moved).RootElement.GetProperty("features")[0]
                .GetProperty("geometry").GetProperty("coordinates")[0].GetDouble(), 4);

            // Refused to a caller who may not edit, in the version asked for.
            (HttpStatusCode anonymous, string refusal) = await SendAsync(HttpMethod.Post, "/wfs", Delete(layer, text, renamed), signedIn: false);
            Assert.NotEqual(HttpStatusCode.OK, anonymous);
            Assert.Contains("ExceptionReport", refusal, StringComparison.Ordinal);

            probe = renamed;
        }
        finally
        {
            (HttpStatusCode deleted, string deleteBody) = await SendAsync(HttpMethod.Post, "/wfs", Delete(layer, text, probe));
            Assert.True(deleted == HttpStatusCode.OK, deleteBody);
            Assert.Equal("1", XDocument.Parse(deleteBody).Descendants().First(e => e.Name.LocalName == "totalDeleted").Value);
        }

        (_, string gone) = await SendAsync(HttpMethod.Get, $"{items}?{text}={probe}");
        Assert.Equal(0, JsonDocument.Parse(gone).RootElement.GetProperty("numberReturned").GetInt32());
    }

    /// <summary>
    /// Transaction is offered to a caller who may edit and not to one who may not — an anonymous client is not told it
    /// may edit and then refused, which is how OGC's suite, calling anonymously, found it.
    /// </summary>
    [Fact]
    public async Task Transaction_is_offered_to_a_caller_who_may_edit_and_not_to_one_who_may_not()
    {
        static string? Transactional(string body) => XDocument.Parse(body).Descendants()
            .Where(e => e.Name.LocalName == "Constraint" && (string?)e.Attribute("name") == "ImplementsTransactionalWFS")
            .Select(e => e.Elements().First(c => c.Name.LocalName == "DefaultValue").Value).FirstOrDefault();

        (_, string signedIn) = await SendAsync(HttpMethod.Get, "/wfs?service=WFS&request=GetCapabilities&version=2.0.0");
        (_, string anonymous) = await SendAsync(HttpMethod.Get, "/wfs?service=WFS&request=GetCapabilities&version=2.0.0", signedIn: false);

        Assert.Equal("TRUE", Transactional(signedIn));
        Assert.Equal("FALSE", Transactional(anonymous));
        Assert.DoesNotContain("name=\"Transaction\"", anonymous, StringComparison.Ordinal);
    }

    /// <summary>
    /// A feature as GetFeature gives it can be inserted, and a value the type cannot hold is InvalidValue — what OGC's WFS
    /// 2.0 Transactional class found the first time it ran as an editor, 2026-10-06.
    /// </summary>
    [Fact]
    public async Task A_feature_carrying_gml_s_own_properties_and_its_ids_is_inserted_and_a_bad_value_is_invalid()
    {
        string layer = Layer();
        string items = $"/ogc/features/v1/collections/{Uri.EscapeDataString(layer)}/items";
        (_, string page) = await SendAsync(HttpMethod.Get, $"{items}?limit=1");
        string text = JsonDocument.Parse(page).RootElement.GetProperty("features")[0].GetProperty("properties")
            .EnumerateObject().First(p => p.Value.ValueKind == JsonValueKind.String && p.Name != "globalid").Name;
        (_, string schema) = await SendAsync(HttpMethod.Get,
            $"/wfs?service=WFS&version=2.0.0&request=DescribeFeatureType&typeNames=graticula:{layer}");
        string geometry = XDocument.Parse(schema).Descendants()
            .First(e => e.Name.LocalName == "element" && ((string?)e.Attribute("type") ?? "").StartsWith("gml:", StringComparison.Ordinal))
            .Attribute("name")!.Value;

        string probe = $"wfsg-{Guid.NewGuid():N}"[..14];

        // gml:identifier, gml:name and an objectid and globalid copied from another feature: passed over, not refused,
        // and the server assigns this feature its own ids.
        (HttpStatusCode status, string body) = await SendAsync(HttpMethod.Post, "/wfs",
            $"<wfs:Transaction service=\"WFS\" version=\"2.0.0\" xmlns:wfs=\"{Wfs20}\" xmlns:gml=\"http://www.opengis.net/gml/3.2\" "
            + "xmlns:graticula=\"urn:graticula:ns\"><wfs:Insert>"
            + $"<graticula:{layer} gml:id=\"x-1\"><gml:identifier codeSpace=\"http://cite.opengeospatial.org/\">{Guid.NewGuid()}</gml:identifier>"
            + "<gml:name>copied</gml:name><graticula:objectid>1</graticula:objectid>"
            + "<graticula:globalid>{00000000-0000-0000-0000-000000000001}</graticula:globalid>"
            + $"<graticula:{text}>{probe}</graticula:{text}><graticula:{geometry}>"
            + "<gml:Point srsName=\"urn:ogc:def:crs:EPSG::4326\"><gml:pos>39.9601 32.8712</gml:pos></gml:Point>"
            + $"</graticula:{geometry}></graticula:{layer}></wfs:Insert></wfs:Transaction>");

        try
        {
            Assert.True(status == HttpStatusCode.OK, body);
            Assert.Equal("1", XDocument.Parse(body).Descendants(XName.Get("totalInserted", Wfs20)).Single().Value);

            // A property the type does not have, and an Update with no filter at all: InvalidValue, because the value is
            // what is wrong.
            foreach (string update in new[]
            {
                "<wfs:Insert><tns:Airport xmlns:tns=\"http://example.org\" gml:id=\"YVR\"><gml:name>Vancouver</gml:name></tns:Airport></wfs:Insert>",
                $"<wfs:Update typeName=\"graticula:{layer}\"><wfs:Property><wfs:ValueReference>gml:boundedBy</wfs:ValueReference>"
                    + "<wfs:Value><Point xmlns=\"http://www.opengis.net/kml/2.2\"><coordinates>-123.1,49.25</coordinates></Point></wfs:Value>"
                    + "</wfs:Property></wfs:Update>",
                $"<wfs:Update typeName=\"graticula:{layer}\"><wfs:Property><wfs:ValueReference>no_such_property</wfs:ValueReference>"
                    + "<wfs:Value>1</wfs:Value></wfs:Property>" + Equal("http://www.opengis.net/fes/2.0", "fes", text, probe) + "</wfs:Update>",
            })
            {
                (HttpStatusCode refused, string report) = await SendAsync(HttpMethod.Post, "/wfs",
                    $"<wfs:Transaction service=\"WFS\" version=\"2.0.0\" xmlns:wfs=\"{Wfs20}\" xmlns:gml=\"http://www.opengis.net/gml/3.2\" "
                    + $"xmlns:graticula=\"urn:graticula:ns\">{update}</wfs:Transaction>");
                Assert.NotEqual(HttpStatusCode.OK, refused);
                Assert.Equal("InvalidValue", XDocument.Parse(report).Descendants().First(e => e.Name.LocalName == "Exception")
                    .Attribute("exceptionCode")!.Value);
            }
        }
        finally
        {
            await SendAsync(HttpMethod.Post, "/wfs", Delete(layer, text, probe));
        }
    }

    private async Task<string> InsertAsync(string layer, string text, string geometry, string probe)
    {
        (HttpStatusCode status, string body) = await SendAsync(HttpMethod.Post, "/wfs",
            $"<wfs:Transaction service=\"WFS\" version=\"2.0.0\" xmlns:wfs=\"{Wfs20}\" xmlns:gml=\"http://www.opengis.net/gml/3.2\" "
            + "xmlns:graticula=\"urn:graticula:ns\"><wfs:Insert>"
            + $"<graticula:{layer}><graticula:{text}>{probe}</graticula:{text}><graticula:{geometry}>"
            + "<gml:Point srsName=\"urn:ogc:def:crs:EPSG::4326\"><gml:pos>39.9601 32.8712</gml:pos></gml:Point>"
            + $"</graticula:{geometry}></graticula:{layer}></wfs:Insert></wfs:Transaction>");
        Assert.True(status == HttpStatusCode.OK, body);
        XDocument answer = XDocument.Parse(body);
        Assert.Equal(XName.Get("TransactionResponse", Wfs20), answer.Root!.Name);
        Assert.Equal("1", answer.Descendants(XName.Get("totalInserted", Wfs20)).Single().Value);
        return answer.Descendants().First(e => e.Name.LocalName == "ResourceId").Attribute("rid")!.Value;
    }

    private static string Delete(string layer, string text, string value) =>
        $"<wfs:Transaction service=\"WFS\" version=\"2.0.0\" xmlns:wfs=\"{Wfs20}\" xmlns:graticula=\"urn:graticula:ns\">"
        + $"<wfs:Delete typeName=\"graticula:{layer}\">" + Equal("http://www.opengis.net/fes/2.0", "fes", text, value)
        + "</wfs:Delete></wfs:Transaction>";
}
