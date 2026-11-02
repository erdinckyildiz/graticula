using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Graticula.Platform.Catalog;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// Portal content adds as doors onto Studio's import — ADR-257, Q-225: the parts that decide something without a
/// server. What the doors call is Studio's own handlers, which the conformance suite drives through them
/// (<c>PortalContentAddsTests</c>).
/// </summary>
public sealed class PortalContentAddsAreStudiosImportTests
{
    private static byte[] Zip(params string[] entries)
    {
        using MemoryStream bytes = new();

        using (ZipArchive zip = new(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string entry in entries)
            {
                using Stream into = zip.CreateEntry(entry).Open();
                into.Write("x"u8);
            }
        }

        return bytes.ToArray();
    }

    [Theory]
    [InlineData("CSV", "csv")]
    [InlineData("Shapefile", "shapefile")]
    [InlineData("File Geodatabase", "fileGeodatabase")]
    [InlineData("GeoJson", "geojson")]
    [InlineData("Microsoft Excel", "excel")]
    [InlineData("csv", "csv")]
    public void Each_data_item_type_publishes_as_the_file_type_ArcGIS_names(string itemType, string fileType)
    {
        Assert.Equal(fileType, PortalEndpoints.FileTypeOf(itemType));
    }

    [Theory]
    [InlineData("Service Definition")]
    [InlineData("Web Map")]
    [InlineData("PDF")]
    [InlineData(null)]
    public void Anything_else_is_not_a_data_item(string? itemType)
    {
        Assert.Null(PortalEndpoints.FileTypeOf(itemType));
    }

    [Theory]
    [InlineData("PDF", true)]
    [InlineData("Microsoft Word", true)]
    [InlineData("Image", true)]
    [InlineData("CSV", true)]
    [InlineData("File Geodatabase", true)]
    [InlineData("Web Map", false)]
    [InlineData("Service Definition", false)]
    [InlineData("Feature Service", false)]
    public void Any_file_Studio_keeps_is_added_through_the_document_door(string itemType, bool file)
    {
        Assert.Equal(file, PortalEndpoints.IsFileItemType(itemType));
    }

    [Fact]
    public void A_zip_is_a_document_only_when_it_is_added_as_a_shapefile_or_a_geodatabase()
    {
        byte[] zip = Zip("parcels.shp", "parcels.dbf", "parcels.shx");

        Assert.Null(Documents.Recognise("parcels.zip", zip, out string? refusal));
        Assert.Contains("Shapefile", refusal, System.StringComparison.Ordinal);

        Assert.Equal("Shapefile", Documents.Recognise("parcels.zip", zip, out _, "Shapefile")!.ArcGisType);
        Assert.Equal("File Geodatabase", Documents.Recognise("parcels.zip", zip, out _, "File Geodatabase")!.ArcGisType);
    }

    [Fact]
    public void What_a_declared_zip_holds_is_looked_at_not_believed()
    {
        DocumentKind shapefile = Documents.Recognise("a.zip", Zip("a.shp"), out _, "Shapefile")!;
        DocumentKind geodatabase = Documents.Recognise("a.zip", Zip("a.shp"), out _, "File Geodatabase")!;

        Assert.Null(HostedDataEndpoints.ArchiveRefusal(shapefile, Zip("roads.shp", "roads.dbf")));
        Assert.NotNull(HostedDataEndpoints.ArchiveRefusal(shapefile, Zip("notes.txt")));
        Assert.NotNull(HostedDataEndpoints.ArchiveRefusal(shapefile, Zip("city.gdb/a00000001.gdbtable")));

        Assert.Null(HostedDataEndpoints.ArchiveRefusal(geodatabase, Zip("city.gdb/a00000001.gdbtable")));
        Assert.NotNull(HostedDataEndpoints.ArchiveRefusal(geodatabase, Zip("roads.shp")));
    }

    [Fact]
    public void GeoJson_is_a_json_object_in_plain_text_named_as_one()
    {
        byte[] json = Encoding.UTF8.GetBytes(" {\"type\":\"FeatureCollection\",\"features\":[]}");

        Assert.Equal("GeoJson", Documents.Recognise("points.geojson", json, out _)!.ArcGisType);
        Assert.Equal("GeoJson", Documents.Recognise("points.json", json, out _, "GeoJson")!.ArcGisType);
        Assert.Null(Documents.Recognise("points.json", json, out _));
        Assert.Null(Documents.Recognise("page.geojson", Encoding.UTF8.GetBytes("<html></html>"), out _));
    }

    [Fact]
    public void A_stored_data_kind_is_known_again_by_its_name()
    {
        foreach (DocumentKind kind in Documents.Data)
        {
            Assert.Same(kind, Documents.Named(kind.Name));
        }
    }

    [Fact]
    public void Publish_parameters_carry_the_name_the_columns_and_the_source_reference()
    {
        PortalEndpoints.DataPublish asked = PortalEndpoints.PublishParametersOf(
            "{\"name\":\"iller\",\"locationType\":\"coordinates\",\"latitudeFieldName\":\"enlem\","
            + "\"longitudeFieldName\":\"boylam\",\"sourceSR\":{\"wkid\":102100,\"latestWkid\":3857},\"maxRecordCount\":2000}",
            out string? error)!;

        Assert.Null(error);
        Assert.Equal(new PortalEndpoints.DataPublish("iller", "coordinates", "boylam", "enlem", 3857), asked);

        Assert.Equal(new PortalEndpoints.DataPublish(null, null, null, null, null), PortalEndpoints.PublishParametersOf(null, out _));
        Assert.Null(PortalEndpoints.PublishParametersOf("[1]", out error));
        Assert.NotNull(error);
        Assert.Null(PortalEndpoints.PublishParametersOf("{", out error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("Turkish provinces (2024)", "Turkish_provinces_2024")]
    [InlineData("  iller  ", "iller")]
    [InlineData("!!!", "layer")]
    public void A_service_is_named_from_its_items_title_as_the_Python_API_names_it(string title, string name)
    {
        Assert.Equal(name, PortalEndpoints.ServiceNameFrom(title));
    }

    [Fact]
    public void A_publish_that_finished_answers_a_job_no_node_looks_up()
    {
        string job = PortalEndpoints.FinishedJob();

        Assert.Equal(32, job.Length);
        Assert.False(System.Guid.TryParse(job, out _));
        Assert.NotEqual(job, PortalEndpoints.FinishedJob());
    }

    [Fact]
    public void The_service_an_import_made_is_read_from_its_own_answer()
    {
        using JsonDocument answer = JsonDocument.Parse(
            "{\"services\":{\"feature\":\"/rest/services/hosted/iller/FeatureServer/0\",\"tiles\":null}}");

        Assert.Equal("iller", PortalEndpoints.ServiceOfImport(answer.RootElement));

        using JsonDocument none = JsonDocument.Parse("{\"id\":1}");
        Assert.Null(PortalEndpoints.ServiceOfImport(none.RootElement));
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void A_layer_definition_the_Python_API_sends_is_designed_in_its_own_reference()
    {
        Assert.True(PortalEndpoints.TryReadLayerDesign(
            Json("""
                {"name":"points","type":"Feature Layer","geometryType":"esriGeometryPoint","objectIdField":"OBJECTID",
                 "hasAttachments":false,"hasZ":true,"extent":{"xmin":0,"ymin":0,"xmax":1,"ymax":1,"spatialReference":{"wkid":4326}},
                 "fields":[{"name":"OBJECTID","type":"esriFieldTypeOID"},{"name":"label","type":"esriFieldTypeString","length":64,"domain":null}]}
                """),
            string.Empty, "svc", null, out HostedDataEndpoints.LayerDesign? design, out string? error), error);

        Assert.Equal("points", design!.Name);
        Assert.Equal("PointZ", design.GeometryType);
        Assert.Equal("svc", design.ServiceName);
        Assert.Equal(4326, design.Srid);
        Assert.Equal(["label"], design.Fields!.Select(f => f.Name));
    }

    [Fact]
    public void Web_Mercator_is_read_by_its_EPSG_code_and_a_table_takes_none()
    {
        Assert.True(PortalEndpoints.TryReadLayerDesign(
            Json("""{"name":"lines","geometryType":"esriGeometryPolyline","spatialReference":{"wkid":102100}}"""),
            string.Empty, "svc", null, out HostedDataEndpoints.LayerDesign? lines, out _));
        Assert.Equal(3857, lines!.Srid);
        Assert.Equal("MultiLineString", lines.GeometryType);

        Assert.True(PortalEndpoints.TryReadLayerDesign(
            Json("""{"name":"visits","type":"Table"}"""), string.Empty, "svc", 5254, out HostedDataEndpoints.LayerDesign? table, out _));
        Assert.Equal("Table", table!.GeometryType);
        Assert.Null(table.Srid);
    }

    [Theory]
    [InlineData("""{"name":"a","geometryType":"esriGeometryPoint","drawingInfo":{"renderer":{}}}""", "drawingInfo")]
    [InlineData("""{"name":"a","geometryType":"esriGeometryPoint","hasAttachments":true}""", "attachments")]
    [InlineData("""{"name":"a","geometryType":"esriGeometryPoint","templates":[]}""", "templates")]
    [InlineData("""{"name":"a","geometryType":"esriGeometryEnvelope"}""", "esriGeometryEnvelope")]
    [InlineData("""{"geometryType":"esriGeometryPoint"}""", "name")]
    public void What_a_layer_is_not_made_from_is_refused_by_name(string definition, string named)
    {
        Assert.False(PortalEndpoints.TryReadLayerDesign(Json(definition), string.Empty, "svc", null, out _, out string? error));
        Assert.Contains(named, error, System.StringComparison.Ordinal);
    }
}
