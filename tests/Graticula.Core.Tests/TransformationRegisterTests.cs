using System.Linq;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>ADR-160: the transformations between two references, from the register generated from PROJ's.</summary>
public sealed class TransformationRegisterTests
{
    // Turkey, in degrees.
    private static readonly Envelope Turkey = new(26, 36, 45, 42);

    [Fact]
    public void Without_a_place_the_transformation_for_the_whole_ground_comes_before_a_regional_grid()
    {
        TransformationPath first = TransformationRegister.Paths(4230, 4326, null)[0];

        Assert.Single(first.Steps);
        Assert.Equal(1133, first.Steps[0].Transformation.Code);
        Assert.True(first.Steps[0].Forward);
    }

    [Fact]
    public void A_place_keeps_only_what_applies_there_most_accurate_first()
    {
        var paths = TransformationRegister.Paths(4230, 4326, Turkey);

        Assert.NotEmpty(paths);
        Assert.All(paths, p => Assert.True(p.Steps[0].Transformation.Area.MaxX >= 26 && p.Steps[0].Transformation.Area.MinX <= 45));
        Assert.DoesNotContain(paths, p => p.Steps[0].Transformation.Name.Contains("Catalonia"));
        double?[] accuracies = [.. paths.Select(p => p.Accuracy)];
        Assert.Equal(accuracies.OrderBy(a => a ?? double.MaxValue), accuracies);
    }

    [Fact]
    public void Used_backwards_it_says_so_and_two_references_without_one_are_joined_through_wgs_84()
    {
        Assert.Contains(TransformationRegister.Paths(4326, 4230, null), p => p.Steps.Count == 1 && p.Steps[0].Transformation.Code == 1133 && !p.Steps[0].Forward);

        var joined = TransformationRegister.Paths(4230, 4269, null);
        Assert.Contains(joined, p => p.Steps.Count == 2 && p.Steps[0].Transformation.Target == 4326 && p.Steps[1].Transformation.Source == 4326);
        Assert.Empty(TransformationRegister.Paths(4326, 4326, null));
    }

    [Fact]
    public void A_transformation_is_found_by_its_code_and_named_to_postgis_by_its_authority()
    {
        DatumTransformation nad = TransformationRegister.Find(1188)!;

        Assert.Equal("NAD83 to WGS 84 (1)", nad.Name);
        Assert.Equal(4, nad.Accuracy);
        Assert.Equal("urn:ogc:def:coordinateOperation:EPSG::1188", nad.Urn);
        Assert.Equal("urn:ogc:def:coordinateOperation:ESRI::108190", TransformationRegister.Find(108190)!.Urn);
        Assert.True(TransformationRegister.Find(1241)!.NeedsGrid);
        Assert.Null(TransformationRegister.Find(1));
        Assert.True(TransformationRegister.Count > 1000);
    }
}
