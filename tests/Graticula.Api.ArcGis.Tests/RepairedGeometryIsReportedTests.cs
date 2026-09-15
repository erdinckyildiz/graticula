using System.Linq;
using System.Text.Json;
using Graticula.Features;
using Xunit;

namespace Graticula.Api.ArcGis.Tests;

/// <summary>
/// A feature whose polygon was stored repaired says so in its result, and no other result carries the field —
/// Q-153, owner decision 2026-09-15.
/// </summary>
public sealed class RepairedGeometryIsReportedTests
{
    [Fact]
    public void Only_a_repaired_geometry_carries_the_field()
    {
        EditOutcome outcome = new(
            [EditResult.Ok(4) with { GeometryRepaired = true }, EditResult.Ok(5)],
            [],
            [],
            RolledBack: false);

        JsonElement[] adds = [.. JsonDocument.Parse(JsonSerializer.Serialize(
                ApplyEditsResponse.Build(outcome, new ApplyEditsRequest.Parsed(new EditBatch([], [], []), [], [], []))))
            .RootElement.GetProperty("addResults").EnumerateArray()];

        Assert.True(adds[0].GetProperty("success").GetBoolean());
        Assert.True(adds[0].GetProperty("geometryRepaired").GetBoolean());
        Assert.False(adds[1].TryGetProperty("geometryRepaired", out _));
    }
}
