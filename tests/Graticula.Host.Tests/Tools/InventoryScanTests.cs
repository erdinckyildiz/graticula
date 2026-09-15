using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Host.Tools;
using Xunit;
using Verdict = Graticula.Host.Tools.InventoryScan.Verdict;

namespace Graticula.Host.Tests.Tools;

/// <summary>
/// The inventory walks an ArcGIS Server's directory and gives every service and layer a verdict with its
/// reason — Q-16.
/// </summary>
/// <remarks>Written 2026-09-15, with the command. The same command was run against the showcase, which is
/// itself an ArcGIS-compatible directory, and read back.</remarks>
public sealed class InventoryScanTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void A_plain_polygon_layer_comes_across()
    {
        InventoryScan.LayerReport report = InventoryScan.Classify(0, Json("""
            {"name":"parcels","geometryType":"esriGeometryPolygon","hasZ":false,
             "fields":[{"name":"OBJECTID","type":"esriFieldTypeOID"},{"name":"code","type":"esriFieldTypeString"}],
             "drawingInfo":{"renderer":{"type":"uniqueValue"}}}
            """), isTable: false);

        Assert.Equal(Verdict.Comes, report.Verdict);
    }

    [Theory]
    [InlineData("""{"name":"z","geometryType":"esriGeometryPolyline","hasZ":true}""", "Partly", "D-107")]
    [InlineData("""{"name":"f","geometryType":"esriGeometryPoint","fields":[{"name":"doc","type":"esriFieldTypeXML"}]}""", "Partly", "doc (esriFieldTypeXML)")]
    [InlineData("""{"name":"h","geometryType":"esriGeometryPoint","drawingInfo":{"renderer":{"type":"heatmap"}}}""", "Partly", "heatmap")]
    [InlineData("""{"name":"v","geometryType":"esriGeometryPoint","isDataVersioned":true}""", "Partly", "versioned")]
    [InlineData("""{"name":"m","geometryType":"esriGeometryMultiPatch"}""", "Stays", "esriGeometryMultiPatch")]
    [InlineData("""{"name":"t"}""", "Stays", "table without geometry")]
    public void What_does_not_come_across_is_named(string layer, string verdict, string reason)
    {
        Verdict expected = Enum.Parse<Verdict>(verdict);
        InventoryScan.LayerReport report = InventoryScan.Classify(3, Json(layer), isTable: false);

        Assert.Equal(expected, report.Verdict);
        Assert.Contains(report.Notes, note => note.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void A_worse_reason_is_not_softened_by_a_milder_one()
    {
        InventoryScan.LayerReport report = InventoryScan.Classify(0, Json(
            """{"name":"x","geometryType":"esriGeometryMultiPatch","hasZ":true}"""), isTable: false);

        Assert.Equal(Verdict.Stays, report.Verdict);
    }

    [Fact]
    public async Task The_directory_is_walked_through_its_folders_with_the_token_and_each_service_is_judged()
    {
        Dictionary<string, string> pages = new(StringComparer.Ordinal)
        {
            ["/arcgis/rest/services"] = """{"folders":["Water"],"services":[{"name":"Imagery","type":"ImageServer"}]}""",
            ["/arcgis/rest/services/Water"] = """{"services":[{"name":"Water/Network","type":"FeatureServer"},{"name":"Water/Routing","type":"NAServer"}]}""",
            ["/arcgis/rest/services/Water/Network/FeatureServer"] = """
                {"capabilities":"Query,Sync","layers":[{"id":0,"name":"Mains"},{"id":1,"name":"Group","subLayerIds":[0]}],
                 "tables":[{"id":2,"name":"Inspections"}]}
                """,
            ["/arcgis/rest/services/Water/Network/FeatureServer/0"] = """{"name":"Mains","geometryType":"esriGeometryPolyline","hasZ":true,"hasAttachments":true}""",
            ["/arcgis/rest/services/Water/Network/FeatureServer/2"] = """{"name":"Inspections","type":"Table"}""",
        };

        List<string> asked = [];
        using HttpClient http = new(new Pages(pages, asked));

        IReadOnlyList<InventoryScan.ServiceReport> services = await InventoryScan.ScanAsync(
            http, new Uri("https://gis.example/arcgis"), "t0ken", CancellationToken.None);

        Assert.All(asked, url => Assert.Contains("token=t0ken", url, StringComparison.Ordinal));
        Assert.Equal(["Imagery", "Water/Network", "Water/Routing"], services.Select(s => s.Name).Order());

        InventoryScan.ServiceReport network = services.Single(s => s.Name == "Water/Network");
        Assert.Equal(Verdict.Stays, network.Verdict);
        Assert.Contains(network.Notes, n => n.Contains("Sync", StringComparison.Ordinal));
        Assert.Equal([0, 2], network.Layers.Select(l => l.Id));
        Assert.Equal(Verdict.Partly, network.Layers[0].Verdict);
        Assert.Equal(Verdict.Stays, network.Layers[1].Verdict);

        Assert.Equal(Verdict.Stays, services.Single(s => s.Type == "ImageServer").Verdict);

        string text = InventoryScan.Report(new Uri("https://gis.example/arcgis"), services);
        Assert.Contains("[not served] Imagery (ImageServer)", text, StringComparison.Ordinal);
        Assert.Contains("PostGIS", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_answered_with_200_and_an_error_object_is_a_refusal()
    {
        Dictionary<string, string> pages = new(StringComparer.Ordinal)
        {
            ["/arcgis/rest/services"] = """{"services":[{"name":"Secret","type":"FeatureServer"}]}""",
            ["/arcgis/rest/services/Secret/FeatureServer"] = """{"error":{"code":499,"message":"Token Required"}}""",
        };

        using HttpClient http = new(new Pages(pages, []));

        InventoryScan.ServiceReport secret = Assert.Single(await InventoryScan.ScanAsync(
            http, new Uri("https://gis.example/arcgis/rest/services"), null, CancellationToken.None));

        Assert.Equal(Verdict.Stays, secret.Verdict);
        Assert.Contains("Token Required", Assert.Single(secret.Notes), StringComparison.Ordinal);
    }

    /// <summary>Answers each path with its page, and 404 otherwise.</summary>
    private sealed class Pages(IReadOnlyDictionary<string, string> pages, List<string> asked) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            asked.Add(request.RequestUri!.ToString());

            return Task.FromResult(pages.TryGetValue(request.RequestUri.AbsolutePath, out string? page)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":{\"code\":404,\"message\":\"no\"}}") });
        }
    }
}
