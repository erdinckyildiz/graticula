using Xunit;

namespace Graticula.Api.ArcGis.Tests;

/// <summary>
/// <c>hasStaticData</c> is true only for a document that offers no way to change the data.
/// </summary>
/// <remarks>
/// Written 2026-09-15: it was true on every layer and service, editable ones included, and an ArcGIS
/// client may keep what it drew from a layer that says its data does not change.
/// </remarks>
public sealed class StaticDataTests
{
    [Theory]
    [InlineData("Query", true)]
    [InlineData("", true)]
    [InlineData("Query,Extract", true)]
    [InlineData("Create,Delete,Query,Update,Editing", false)]
    [InlineData("Query,Update", false)]
    [InlineData("Create,Query", false)]
    public void Only_a_layer_nothing_can_edit_says_its_data_is_static(string capabilities, bool isStatic)
    {
        Assert.Equal(isStatic, FeatureServerMetadataWriter.IsStatic(capabilities));
    }
}
