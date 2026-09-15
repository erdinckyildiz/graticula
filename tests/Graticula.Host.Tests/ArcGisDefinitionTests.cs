using System.Collections.Generic;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// <c>addToDefinition</c> and <c>deleteFromDefinition</c> read fields and refuse everything else by name.
/// </summary>
/// <remarks>Written 2026-09-15, when ArcGIS's layer administration operations were first answered.</remarks>
public sealed class ArcGisDefinitionTests
{
    [Fact]
    public void Fields_are_read_with_their_hosted_types_and_a_string_keeps_its_length()
    {
        Assert.True(ArcGisAdminEndpoints.TryDefinition(
            """{"fields":[{"name":"status","type":"esriFieldTypeString","alias":"Durum","length":40,"nullable":true},{"name":"n","type":"esriFieldTypeInteger"}]}""",
            adding: true, out List<HostedDataEndpoints.FieldDesign> fields, out string? error), error);

        Assert.Equal(new HostedDataEndpoints.FieldDesign("status", "Text", true, 40), fields[0]);
        Assert.Equal(new HostedDataEndpoints.FieldDesign("n", "Integer", true, null), fields[1]);
    }

    [Theory]
    [InlineData("""{"fields":[{"name":"a","type":"esriFieldTypeString"}],"indexes":[]}""", "indexes")]
    [InlineData("""{"fields":[{"name":"a","type":"esriFieldTypeBlob"}]}""", "esriFieldTypeBlob")]
    [InlineData("""{"fields":[{"name":"a","type":"esriFieldTypeGlobalID"}]}""", "global-ids")]
    [InlineData("""{"fields":[{"name":"a","type":"esriFieldTypeString","nullable":false}]}""", "nullable false")]
    [InlineData("""{"fields":[{"name":"a","type":"esriFieldTypeString","domain":{"type":"codedValue"}}]}""", "domain")]
    [InlineData("""{"fields":[{"name":"a"}]}""", "no 'type'")]
    [InlineData("""{"fields":[]}""", "names no fields")]
    [InlineData("not json", "not valid JSON")]
    public void A_definition_that_would_be_half_applied_is_refused_whole(string json, string reason)
    {
        Assert.False(ArcGisAdminEndpoints.TryDefinition(json, adding: true, out _, out string? error));
        Assert.Contains(reason, error, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_delete_needs_only_names()
    {
        Assert.True(ArcGisAdminEndpoints.TryDefinition(
            """{"fields":[{"name":"status"}]}""", adding: false, out List<HostedDataEndpoints.FieldDesign> fields, out _));

        Assert.Equal("status", Assert.Single(fields).Name);
    }
}
