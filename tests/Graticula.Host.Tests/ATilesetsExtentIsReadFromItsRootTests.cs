using System;
using System.Text.Json.Nodes;
using Graticula.Host;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-278 condition 4: a 3D Tiles archive's extent, for the portal's <c>bbox</c> search, from its root's bounding volume
/// — a region, or a box or sphere placed on the earth by the root's transform.
/// </summary>
public sealed class ATilesetsExtentIsReadFromItsRootTests
{
    private const double A = 6378137.0;
    private const double E2 = 0.00669437999014;

    private static (double X, double Y, double Z) Ecef(double lon, double lat)
    {
        double l = lon * Math.PI / 180, p = lat * Math.PI / 180;
        double n = A / Math.Sqrt(1 - (E2 * Math.Sin(p) * Math.Sin(p)));
        return (n * Math.Cos(p) * Math.Cos(l), n * Math.Cos(p) * Math.Sin(l), n * (1 - E2) * Math.Sin(p));
    }

    /// <summary>East-north-up at a place, column-major as 3D Tiles writes it.</summary>
    private static double[] EastNorthUp(double lon, double lat)
    {
        double l = lon * Math.PI / 180, p = lat * Math.PI / 180;
        (double x, double y, double z) = Ecef(lon, lat);
        return
        [
            -Math.Sin(l), Math.Cos(l), 0, 0,
            -Math.Sin(p) * Math.Cos(l), -Math.Sin(p) * Math.Sin(l), Math.Cos(p), 0,
            Math.Cos(p) * Math.Cos(l), Math.Cos(p) * Math.Sin(l), Math.Sin(p), 0,
            x, y, z, 1,
        ];
    }

    private static JsonObject Tileset(JsonObject volume, double[]? transform = null)
    {
        JsonObject root = new() { ["boundingVolume"] = volume, ["geometricError"] = 0 };
        if (transform is not null)
        {
            root["transform"] = new JsonArray([.. Array.ConvertAll(transform, d => (JsonNode)JsonValue.Create(d))]);
        }

        return new JsonObject { ["asset"] = new JsonObject { ["version"] = "1.1" }, ["root"] = root };
    }

    private static JsonArray Numbers(params double[] values) => new([.. Array.ConvertAll(values, d => (JsonNode)JsonValue.Create(d))]);

    [Fact]
    public void A_region_is_read_as_it_is()
    {
        double[][] extent = SceneServerEndpoints.TilesetExtentOf(Tileset(new JsonObject
        {
            ["region"] = Numbers(29 * Math.PI / 180, 41 * Math.PI / 180, 29.01 * Math.PI / 180, 41.01 * Math.PI / 180, 0, 50),
        }));
        Assert.Equal(29, extent[0][0], 9);
        Assert.Equal(41.01, extent[1][1], 9);
    }

    [Fact]
    public void A_box_placed_by_its_transform_is_where_the_transform_puts_it()
    {
        // A 200 m box around Istanbul's 29°E 41°N, in east-north-up metres.
        double[][] extent = SceneServerEndpoints.TilesetExtentOf(Tileset(
            new JsonObject { ["box"] = Numbers(0, 0, 0, 100, 0, 0, 0, 100, 0, 0, 0, 100) }, EastNorthUp(29, 41)));
        Assert.Equal(2, extent.Length);
        Assert.InRange(extent[0][0], 28.997, 28.9995);
        Assert.InRange(extent[1][0], 29.0005, 29.003);
        Assert.InRange(extent[0][1], 40.998, 40.9995);
        Assert.InRange(extent[1][1], 41.0005, 41.002);
    }

    [Fact]
    public void A_sphere_in_earth_centred_metres_is_read_without_a_transform()
    {
        (double x, double y, double z) = Ecef(-74, 40.7);
        double[][] extent = SceneServerEndpoints.TilesetExtentOf(Tileset(new JsonObject { ["sphere"] = Numbers(x, y, z, 500) }));
        Assert.Equal(2, extent.Length);
        Assert.True(extent[0][0] < -74 && extent[1][0] > -74 && extent[1][0] - extent[0][0] < 0.05, string.Join(",", extent[0]) + " " + string.Join(",", extent[1]));
        Assert.True(extent[0][1] < 40.7 && extent[1][1] > 40.7 && extent[1][1] - extent[0][1] < 0.05);
    }

    [Fact]
    public void A_box_with_no_place_on_the_earth_has_no_extent()
    {
        Assert.Empty(SceneServerEndpoints.TilesetExtentOf(Tileset(new JsonObject { ["box"] = Numbers(0, 0, 0, 25, 0, 0, 0, 25, 0, 0, 0, 25) })));
        Assert.Empty(SceneServerEndpoints.TilesetExtentOf(Tileset(new JsonObject { ["sphere"] = Numbers(1, 2) })));
    }
}
