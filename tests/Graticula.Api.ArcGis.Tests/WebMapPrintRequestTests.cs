using System.Linq;
using Graticula.Api.ArcGis;
using Xunit;

namespace Graticula.Api.ArcGis.Tests;

/// <summary>
/// <c>Web_Map_as_JSON</c> is read as the ExportWebMap specification writes it — ADR-265 §5.2.
/// </summary>
public sealed class WebMapPrintRequestTests
{
    private const string Sample = """
        {
          "mapOptions": {
            "extent": {"xmin": 3600000, "ymin": 4800000, "xmax": 3700000, "ymax": 4900000, "spatialReference": {"wkid": 102100, "latestWkid": 3857}},
            "scale": 50000,
            "rotation": 15
          },
          "operationalLayers": [
            {"id": "parcels", "title": "Parcels", "url": "https://gis.example/rest/services/hosted/parcels/MapServer",
             "visibleLayers": [0, 2], "layers": [{"id": 2, "layerDefinition": {"definitionExpression": "kind = 'R'"}}]},
            {"id": "roads", "url": "/rest/services/roads/FeatureServer/0", "visibility": false,
             "layerDefinition": {"definitionExpression": "class < 3", "drawingInfo": {"renderer": {"type": "simple"}}}},
            {"id": "tiles", "type": "VectorTileLayer", "styleUrl": "https://gis.example/rest/services/base/VectorTileServer/resources/styles/root.json"}
          ],
          "baseMap": {"title": "Topographic", "baseMapLayers": [{"url": "https://services.arcgisonline.com/x/MapServer"}]},
          "exportOptions": {"outputSize": [1024, 768], "dpi": 150},
          "layoutOptions": {
            "titleText": "Ankara", "authorText": "GIS office", "copyrightText": "© 2026",
            "scaleBarOptions": {"metricUnit": "esriMeters", "metricLabel": "m", "nonMetricUnit": "esriFeet", "nonMetricLabel": "ft"},
            "legendOptions": {"operationalLayers": [{"id": "parcels", "subLayerIds": [2]}]}
          }
        }
        """;

    [Fact]
    public void The_documented_members_are_read()
    {
        Assert.True(WebMapPrintRequest.TryParse(Sample, out WebMapPrintRequest? print, out string? error), error);

        Assert.Equal(3857, print!.Srid);
        Assert.Equal(3857, print.ExtentSrid);
        Assert.Equal(3_600_000, print.Extent.MinX);
        Assert.Equal(50_000, print.Scale);
        Assert.Equal(15, print.Rotation);
        Assert.Equal((1024, 768, 150.0), (print.OutputWidth, print.OutputHeight, print.Dpi));
        Assert.Equal(("Ankara", "GIS office", "© 2026"), (print.Title, print.Author, print.Copyright));
        Assert.Equal(("esriMeters", "m", "esriFeet", "ft"),
            (print.Units.MetricUnit, print.Units.MetricLabel, print.Units.NonMetricUnit, print.Units.NonMetricLabel));
        Assert.Equal([2], print.LegendLayers!["parcels"]!);

        // The basemap is drawn first, under everything.
        Assert.True(print.Layers[0].IsBasemap);
        WebMapPrintLayer parcels = print.Layers[1];
        Assert.Equal([0, 2], parcels.VisibleLayers!);
        Assert.Equal("kind = 'R'", parcels.Definitions[2]);

        WebMapPrintLayer roads = print.Layers[2];
        Assert.False(roads.Visible);
        Assert.Equal("class < 3", roads.DefinitionExpression);
        Assert.NotNull(roads.DrawingInfo);

        // A vector tile layer that names only its style is found by the style's address.
        Assert.EndsWith("VectorTileServer/resources/styles/root.json", print.Layers[3].Url!, System.StringComparison.Ordinal);
        Assert.Equal("VectorTileLayer", print.Layers[3].Kind);
    }

    [Fact]
    public void Without_export_options_the_map_is_printed_at_96_dpi()
    {
        Assert.True(WebMapPrintRequest.TryParse(
            """{"mapOptions": {"extent": {"xmin": 0, "ymin": 0, "xmax": 1, "ymax": 1, "spatialReference": {"wkid": 4326}}}}""",
            out WebMapPrintRequest? print, out _));

        Assert.Equal(96, print!.Dpi);
        Assert.Empty(print.Layers);
        Assert.Null(print.LegendLayers);
    }

    [Theory]
    [InlineData("", "required")]
    [InlineData("[1]", "JSON object")]
    [InlineData("{", "not JSON")]
    [InlineData("""{"mapOptions": {}}""", "mapOptions.extent")]
    [InlineData("""{"mapOptions": {"extent": {"xmin": 1, "ymin": 0, "xmax": 0, "ymax": 1, "spatialReference": {"wkid": 4326}}}}""", "max above its min")]
    [InlineData("""{"mapOptions": {"extent": {"xmin": 0, "ymin": 0, "xmax": 1, "ymax": 1, "spatialReference": {"wkt": "GEOGCS[]"}}}}""", "wkid")]
    [InlineData("""{"mapOptions": {"extent": {"xmin": 0, "ymin": 0, "xmax": 1, "ymax": 1, "spatialReference": {"wkid": 4326}}}, "exportOptions": {"dpi": 600}}""", "at most 300 dpi")]
    [InlineData("""{"mapOptions": {"extent": {"xmin": 0, "ymin": 0, "xmax": 1, "ymax": 1, "spatialReference": {"wkid": 4326}}}, "exportOptions": {"outputSize": [10]}}""", "outputSize")]
    public void A_print_that_cannot_be_drawn_is_refused_with_what_is_wrong(string json, string said)
    {
        Assert.False(WebMapPrintRequest.TryParse(json, out _, out string? error));
        Assert.Contains(said, error, System.StringComparison.Ordinal);
    }

    [Fact]
    public void More_layers_than_one_print_draws_are_refused()
    {
        string layers = string.Join(",", Enumerable.Range(0, WebMapPrintRequest.MaximumLayers + 1).Select(i => $$"""{"id": "l{{i}}"}"""));
        string json = """{"mapOptions": {"extent": {"xmin": 0, "ymin": 0, "xmax": 1, "ymax": 1, "spatialReference": {"wkid": 4326}}}, "operationalLayers": ["""
            + layers + "]}";

        Assert.False(WebMapPrintRequest.TryParse(json, out _, out string? error));
        Assert.Contains("at most 64", error, System.StringComparison.Ordinal);
    }
}
