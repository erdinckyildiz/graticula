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
}
