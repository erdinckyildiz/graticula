using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// Reading the ordinates a geometry type name declares — ADR-074.
/// </summary>
/// <remarks>
/// The suffix rule decides two things a person sees: whether a three-dimensional table can be
/// published at all, and whether the layer document offers geometry editing that every save
/// would refuse. Both run off this one parse, so the case that is easy to get backwards —
/// <c>ZM</c>, which also ends in <c>M</c> — is the case with a test of its own.
/// </remarks>
public sealed class GeometryOrdinatesTests
{
    [Theory]
    [InlineData("Point", GeometryOrdinates.None)]
    [InlineData("POINT", GeometryOrdinates.None)]
    [InlineData("Geometry", GeometryOrdinates.None)]
    [InlineData("", GeometryOrdinates.None)]
    [InlineData(null, GeometryOrdinates.None)]
    [InlineData("PointZ", GeometryOrdinates.Z)]
    [InlineData("MULTILINESTRINGZ", GeometryOrdinates.Z)]
    [InlineData("PointM", GeometryOrdinates.M)]
    [InlineData("PointZM", GeometryOrdinates.Z | GeometryOrdinates.M)]
    [InlineData("multipolygonzm", GeometryOrdinates.Z | GeometryOrdinates.M)]
    public void The_suffix_is_what_declares_them(string? typeName, GeometryOrdinates expected) =>
        Assert.Equal(expected, Ordinates.OfTypeName(typeName));

    [Theory]
    [InlineData("PointZ", "Point")]
    [InlineData("POINTZM", "POINT")]
    [InlineData("MultiPolygonM", "MultiPolygon")]
    [InlineData("MultiPolygon", "MultiPolygon")]
    [InlineData("Geometry", "Geometry")]
    public void The_kind_underneath_is_the_name_without_it(string typeName, string expected) =>
        Assert.Equal(expected, Ordinates.WithoutOrdinates(typeName));

    /// <summary>
    /// The numbering is PostGIS's <c>ST_Zmflag</c>, which the write path casts rather than maps.
    /// </summary>
    /// <remarks>
    /// <c>PostGisFeatureWriter</c> reads that function per row and casts the integer straight to
    /// <see cref="GeometryOrdinates"/> to name what it is refusing to overwrite. If these two
    /// numberings ever drift, that refusal names the wrong ordinate and nothing else notices.
    /// </remarks>
    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "an M ordinate")]
    [InlineData(2, "a Z ordinate")]
    [InlineData(3, "Z and M ordinates")]
    public void A_zmflag_names_what_it_stands_for(int zmFlag, string? expected) =>
        Assert.Equal(expected, Ordinates.Name((GeometryOrdinates)zmFlag));
}
