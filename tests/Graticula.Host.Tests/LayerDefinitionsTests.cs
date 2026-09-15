using System;
using System.Collections.Generic;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The three spellings of a MapServer <c>layerDefs</c>, and what is refused.
/// </summary>
/// <remarks>
/// Written 2026-09-15, when <c>layerDefs</c> went from refused to evaluated (D-125).
/// </remarks>
public sealed class LayerDefinitionsTests
{
    private static readonly PublishedLayer[] Layers =
    [
        Layer(0),
        Layer(2),
    ];

    private static PublishedLayer Layer(int index) =>
        new(
            Guid.NewGuid(),
            new LayerDefinition("l" + index, "hosted", "l" + index, "geom", 4326, "objectid", "objectid", true),
            "source",
            "Host=localhost;Port=1;Database=nothing",
            GeometryKind.Polygon,
            null,
            SharingScope.Public,
            ServiceStatus.Started,
            layerIndex: index);

    [Theory]
    [InlineData("{\"0\":\"il='Adana'\",\"2\":\"n > 3\"}")]
    [InlineData("[{\"layerId\":0,\"where\":\"il='Adana'\"},{\"layerId\":2,\"where\":\"n > 3\"}]")]
    [InlineData("0:il='Adana';2:n > 3")]
    public void Every_spelling_reads_to_a_clause_per_layer(string raw)
    {
        Assert.True(LayerDefinitions.TryRead(raw, Layers, out Dictionary<int, string> read, out string? error), error);

        Assert.Equal("il='Adana'", read[0]);
        Assert.Equal("n > 3", read[2]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    public void Nothing_sent_is_nothing_to_filter(string? raw)
    {
        Assert.True(LayerDefinitions.TryRead(raw, Layers, out Dictionary<int, string> read, out _));
        Assert.Empty(read);
    }

    [Theory]
    [InlineData("{\"1\":\"a=1\"}", "does not have")]
    [InlineData("{\"0\":", "not JSON")]
    [InlineData("il='Adana'", "not a layer definition")]
    [InlineData("[{\"where\":\"a=1\"}]", "layerId")]
    public void What_cannot_be_read_is_refused_with_the_reason(string raw, string reason)
    {
        Assert.False(LayerDefinitions.TryRead(raw, Layers, out _, out string? error));
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }
}
