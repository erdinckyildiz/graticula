using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Graticula.Host.Tools;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The pieces of <c>graticula tools migrate</c> that decide what a plan says — ADR-081.
/// </summary>
public sealed class MigrationPlanTests
{
    private static readonly MigrationPlan.Table[] Tables =
    [
        new("gis", "parcels", "shape", 2952, "MULTIPOLYGON", "objectid"),
        new("gis", "road_centrelines", "shape", 2952, "MULTILINESTRING", "objectid"),
        new("gis", "wells", "geom", 2952, "POINT", null),
        new("archive", "parcels_2019", "shape", 2952, "MULTIPOLYGON", "objectid"),
        new("gis", "hydrants", "shape", 2952, "POINT", "objectid"),
        new("staging", "hydrants", "shape", 2952, "POINT", "objectid"),
        new("gis", "codes", null, 0, null, "objectid"),
    ];

    [Theory]
    [InlineData("GISDB.GISOWNER.PARCELS", "parcels")]
    [InlineData("Road Centrelines", "roadcentrelines")]
    [InlineData("road_centrelines", "roadcentrelines")]
    public void A_name_is_compared_by_its_last_part_letters_and_digits(string name, string expected)
    {
        Assert.Equal(expected, MigrationPlan.Normalise(name));
    }

    [Fact]
    public void One_table_by_name_is_a_match_and_says_to_check_it()
    {
        (MigrationPlan.Table? table, string note) = MigrationPlan.Match("GISDB.GISOWNER.Parcels", Tables);

        Assert.Equal(("gis", "parcels"), (table!.Schema, table.Name));
        Assert.Contains("Check it", note, StringComparison.Ordinal);

        Assert.Equal("road_centrelines", MigrationPlan.Match("Road Centrelines", Tables).Table!.Name);
    }

    /// <summary>
    /// A guess the tool cannot make safely is left to a person, with the reason — ADR-081 §3.
    /// </summary>
    [Theory]
    [InlineData("Hydrants", "2 tables match")]
    [InlineData("Wells", "no integer object-id column")]
    [InlineData("Manholes", "No table with a geometry column")]
    [InlineData("Codes", "No table with a geometry column")]
    public void No_match_or_several_leaves_the_entry_empty_and_says_why(string layer, string reason)
    {
        (MigrationPlan.Table? table, string note) = MigrationPlan.Match(layer, Tables);

        Assert.Null(table);
        Assert.Contains(reason, note, StringComparison.Ordinal);
    }

    [Fact]
    public void Aliases_are_the_labels_that_differ_from_their_names()
    {
        using JsonDocument layer = JsonDocument.Parse("""
            {"fields":[
              {"name":"objectid","alias":"OBJECTID"},
              {"name":"owner","alias":"owner"},
              {"name":"parcel_no","alias":"Parcel number"},
              {"name":"area","alias":""}
            ]}
            """);

        IReadOnlyDictionary<string, string> aliases = MigrationPlan.AliasesOf(layer.RootElement);

        Assert.Equal(["objectid", "parcel_no"], aliases.Keys.Order());
        Assert.Equal("Parcel number", aliases["parcel_no"]);
    }

    [Fact]
    public void The_plan_carries_what_apply_reads_and_leaves_an_unmatched_entry_empty()
    {
        using JsonDocument drawing = JsonDocument.Parse("""{"renderer":{"type":"simple"}}""");

        string written = MigrationPlan.Write(
            new Uri("https://arcgis.example/arcgis"),
            "cadastre",
            [
                new MigrationPlan.Entry("Cadastre", "Land", 0, "Parcels", Tables[0],
                    new Dictionary<string, string> { ["parcel_no"] = "Parcel number" }, drawing.RootElement, "Matched."),
                new MigrationPlan.Entry("Cadastre", "Land", 1, "Manholes", null,
                    new Dictionary<string, string>(), null, "No table."),
            ]);

        JsonElement plan = JsonDocument.Parse(written).RootElement;
        JsonElement[] layers = [.. plan.GetProperty("layers").EnumerateArray()];

        Assert.Equal("cadastre", plan.GetProperty("dataSource").GetString());
        Assert.Equal("parcels", layers[0].GetProperty("table").GetString());
        Assert.Equal("objectid", layers[0].GetProperty("objectIdColumn").GetString());
        Assert.Equal("Land", layers[0].GetProperty("folder").GetString());
        Assert.Equal("Parcel number", layers[0].GetProperty("aliases").GetProperty("parcel_no").GetString());
        Assert.Equal("simple", layers[0].GetProperty("drawingInfo").GetProperty("renderer").GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, layers[1].GetProperty("table").ValueKind);
    }

    /// <summary>
    /// <c>apply</c> stores a layer's drawing asking for an unusable picture to be kept as a loss — ADR-099 §5.1.
    /// </summary>
    /// <remarks>
    /// A real server's <c>esriPMS</c> may name its picture only by URL; refused, the whole drawing would be lost
    /// and every class drawn in the generated appearance. What the route then does is
    /// <c>PictureMarkerTests.The_migrate_path_keeps_a_url_only_picture_as_a_loss_and_carries_image_data_with_its_picture</c>.
    /// </remarks>
    [Fact]
    public void Apply_asks_the_symbology_route_to_keep_a_picture_it_cannot_use()
    {
        Assert.Equal("?pictures=keep", MigrationPlan.SymbologyQuery);
    }
    /// <summary>
    /// D-385: a tie by name is narrowed by the source layer's geometry kind, and left a tie when that does not settle it.
    /// </summary>
    [Fact]
    public void A_tie_by_name_is_narrowed_by_the_layers_geometry_kind()
    {
        MigrationPlan.Table[] tables =
        [
            new("gis", "assets", "shape", 2952, "MULTIPOLYGON", "objectid"),
            new("survey", "assets", "shape", 2952, "POINT", "objectid"),
        ];

        Assert.Equal("gis", MigrationPlan.Match("Assets", tables, geometryType: "esriGeometryPolygon").Table!.Schema);
        Assert.Equal("survey", MigrationPlan.Match("Assets", tables, geometryType: "esriGeometryPoint").Table!.Schema);
        Assert.Null(MigrationPlan.Match("Assets", tables).Table);
        Assert.Null(MigrationPlan.Match("Assets", tables, geometryType: "esriGeometryPolyline").Table);

        // Two of the same kind stay a tie: the kind narrows, it does not pick.
        Assert.Null(MigrationPlan.Match("Hydrants", Tables, geometryType: "esriGeometryPoint").Table);
    }

    [Theory]
    [InlineData("esriGeometryPolygon", "polygon")]
    [InlineData("MULTIPOLYGON", "polygon")]
    [InlineData("esriGeometryPolyline", "line")]
    [InlineData("MULTILINESTRING", "line")]
    [InlineData("esriGeometryMultipoint", "point")]
    [InlineData("POINT", "point")]
    [InlineData(null, null)]
    [InlineData("GEOMETRY", null)]
    public void A_geometry_kind_is_read_from_esri_and_postgis_names(string? type, string? kind) =>
        Assert.Equal(kind, MigrationPlan.KindOf(type));

    /// <summary>
    /// D-385, ADR-081 Alternative C: the manifest's dataset is the table's own name, so a layer renamed at publish time
    /// finds its table; the dataset's owner settles a tie between schemas.
    /// </summary>
    [Fact]
    public void A_dataset_from_the_manifest_is_matched_in_place_of_the_layers_name()
    {
        (MigrationPlan.Table? table, string note) =
            MigrationPlan.Match("Fire hydrants (2024)", Tables, geometryType: "esriGeometryPoint", dataset: "GISDB.STAGING.HYDRANTS");

        Assert.Equal(("staging", "hydrants"), (table!.Schema, table.Name));
        Assert.Contains("manifest", note, StringComparison.Ordinal);

        // Without the manifest the same layer matches nothing.
        Assert.Null(MigrationPlan.Match("Fire hydrants (2024)", Tables).Table);

        // An owner that is no schema here leaves the tie as it was.
        Assert.Null(MigrationPlan.Match("x", Tables, dataset: "GISDB.SDE.HYDRANTS").Table);
    }

    [Fact]
    public void Layers_take_the_dataset_their_name_matches_and_a_last_one_left_takes_the_last_dataset()
    {
        // Two layers named as their datasets, one renamed: the renamed one takes the dataset left over.
        Assert.Equal(
            "GISDB.GIS.PARCELS|GISDB.GIS.ROAD_CENTRELINES|GISDB.GIS.WATER_MAINS",
            Said(MigrationPlan.PairWithManifest(["Parcels", "Road centrelines", "Pipes"], ["GISDB.GIS.WATER_MAINS", "GISDB.GIS.PARCELS", "GISDB.GIS.ROAD_CENTRELINES"])));

        // A service of one layer and one dataset: whatever the layer is called.
        Assert.Equal("GISDB.GIS.WATER_MAINS", Said(MigrationPlan.PairWithManifest(["Network"], ["GISDB.GIS.WATER_MAINS"])));

        // Two renamed layers and two datasets left: nothing is guessed.
        Assert.Equal("-|-", Said(MigrationPlan.PairWithManifest(["North", "South"], ["GISDB.GIS.A", "GISDB.GIS.B"])));

        // Two datasets of one name: neither layer takes one by name.
        Assert.Equal("-|-", Said(MigrationPlan.PairWithManifest(["Parcels", "Other"], ["DB.A.PARCELS", "DB.B.PARCELS"])));
    }

    private static string Said(string?[] paired) => string.Join("|", paired.Select(p => p ?? "-"));

    [Fact]
    public void A_manifests_datasets_are_its_on_server_names_once_each()
    {
        using JsonDocument manifest = JsonDocument.Parse("""
            {"databases":[
              {"onServerConnectionString":"…","datasets":[{"onServerName":"GISDB.GIS.PARCELS"},{"onServerName":"GISDB.GIS.ROADS"}]},
              {"datasets":[{"onPremiseName":"GISDB.GIS.WELLS"},{"onServerName":"gisdb.gis.parcels"}]}
            ]}
            """);

        Assert.Equal(["GISDB.GIS.PARCELS", "GISDB.GIS.ROADS", "GISDB.GIS.WELLS"], MigrationPlan.DatasetsOf(manifest.RootElement));
        Assert.Empty(MigrationPlan.DatasetsOf(JsonDocument.Parse("""{"error":{"code":403}}""").RootElement));
        Assert.Equal("GISOWNER", MigrationPlan.OwnerOf("GISDB.GISOWNER.PARCELS"));
        Assert.Null(MigrationPlan.OwnerOf("PARCELS"));
        Assert.Equal("https://h/arcgis/admin/services", MigrationPlan.AdminServicesRoot("https://h/arcgis/rest/services"));
    }
}
