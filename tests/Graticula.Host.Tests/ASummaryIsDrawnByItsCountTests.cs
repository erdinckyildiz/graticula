using System.Text.Json;
using Graticula.Cartography;
using Graticula.Geometries;
using Graticula.Host;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-222, design review 2026-10-29: a summary's result is drawn by its count, in up to five classes, so it says
/// something the moment it is opened.
/// </summary>
public sealed class ASummaryIsDrawnByItsCountTests
{
    [Fact]
    public void A_count_up_to_twelve_is_five_classes_of_the_count_field()
    {
        string? drawn = AnalysisRunner.DrawnBy("point_count", 12, GeometryKind.MultiPolygon);

        Assert.NotNull(drawn);
        string esri = SymbologyConversion.ToDrawingInfo(drawn!, "summary", GeometryKind.MultiPolygon).DrawingInfo.ToJsonString();
        JsonElement renderer = JsonDocument.Parse(esri).RootElement.GetProperty("renderer");

        Assert.Equal("classBreaks", renderer.GetProperty("type").GetString());
        Assert.Equal("point_count", renderer.GetProperty("field").GetString());
        Assert.Equal(5, renderer.GetProperty("classBreakInfos").GetArrayLength());
        Assert.Equal(12, renderer.GetProperty("classBreakInfos")[4].GetProperty("classMaxValue").GetDouble());
    }

    [Fact]
    public void A_count_of_two_is_two_classes_and_nothing_counted_is_drawn_as_generated()
    {
        string? drawn = AnalysisRunner.DrawnBy("count", 2, GeometryKind.MultiPolygon);
        string esri = SymbologyConversion.ToDrawingInfo(drawn!, "summary", GeometryKind.MultiPolygon).DrawingInfo.ToJsonString();

        Assert.Equal(2, JsonDocument.Parse(esri).RootElement.GetProperty("renderer").GetProperty("classBreakInfos").GetArrayLength());
        Assert.Null(AnalysisRunner.DrawnBy("count", 0, GeometryKind.MultiPolygon));
        Assert.Null(AnalysisRunner.DrawnBy("count", null, GeometryKind.MultiPolygon));
    }
}
