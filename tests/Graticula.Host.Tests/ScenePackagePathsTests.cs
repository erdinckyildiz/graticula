using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-270 §5.6: one rule takes an I3S REST path to a package entry for every profile — a 3D object or point layer, a
/// building scene layer's sublayers, a point cloud's own extensions.
/// </summary>
public sealed class ScenePackagePathsTests
{
    /// <summary>A package of the entries named, as <see cref="ScenePackages"/> indexes one: by lower-case path.</summary>
    private static ScenePackages.Package Package(params string[] names) =>
        new("unused.slpk", names.ToDictionary(n => n.ToLowerInvariant(), _ => new ScenePackages.Entry(0, 0, 0, 0)));

    private static string? Entry(ScenePackages.Package package, string below) =>
        SceneServerEndpoints.EntryFor(below, package.Find, package.FindStem);

    [Fact]
    public void A_building_scene_layers_sublayer_is_its_own_layer_below_sublayers()
    {
        // The layout of a building scene layer package ArcGIS Pro wrote (Esri's sample, read 2026-11-05).
        ScenePackages.Package package = Package(
            "3dSceneLayer.json.gz", "statistics/summary.json.gz",
            "sublayers/21/3dSceneLayer.json.gz", "sublayers/21/nodepages/0.json.gz",
            "sublayers/21/nodes/4/geometries/1.bin.gz", "sublayers/21/nodes/4/attributes/f_17/0.bin.gz",
            "sublayers/21/nodes/4/3dNodeIndexDocument.json.gz", "sublayers/21/nodes/4/shared/sharedResource.json.gz",
            "sublayers/21/statistics/f_3/0.json.gz");

        Assert.Equal("sublayers/21/3dscenelayer.json.gz", Entry(package, "sublayers/21"));
        Assert.Equal("sublayers/21/nodepages/0.json.gz", Entry(package, "sublayers/21/nodepages/0"));
        Assert.Equal("sublayers/21/nodes/4/geometries/1.bin.gz", Entry(package, "sublayers/21/nodes/4/geometries/1"));
        Assert.Equal("sublayers/21/nodes/4/attributes/f_17/0.bin.gz", Entry(package, "sublayers/21/nodes/4/attributes/f_17/0"));
        Assert.Equal("sublayers/21/nodes/4/3dnodeindexdocument.json.gz", Entry(package, "sublayers/21/nodes/4"));
        Assert.Equal("sublayers/21/nodes/4/shared/sharedresource.json.gz", Entry(package, "sublayers/21/nodes/4/shared"));
        Assert.Equal("statistics/summary.json.gz", Entry(package, "statistics/summary"));
        Assert.Null(Entry(package, "sublayers/22"));
    }

    [Fact]
    public void A_resource_under_an_extension_of_its_own_is_found_by_its_stem()
    {
        // A point cloud stores its geometry and attributes under extensions the named candidates do not list.
        ScenePackages.Package package = Package(
            "nodepages/0.json.gz", "nodes/3/geometries/0.bin.pccxyz", "nodes/3/attributes/2/0.bin.pccrgb");

        Assert.Equal("nodes/3/geometries/0.bin.pccxyz", Entry(package, "nodes/3/geometries/0"));
        Assert.Equal("nodes/3/attributes/2/0.bin.pccrgb", Entry(package, "nodes/3/attributes/2/0"));
        Assert.Equal("nodepages/0.json.gz", Entry(package, "nodepages/0"));
        Assert.Null(Entry(package, "nodes/3/geometries/1"));
    }

    [Fact]
    public void The_layers_own_resources_are_found_as_before()
    {
        ScenePackages.Package package = Package(
            "nodes/1/geometries/0.bin.gz", "nodes/1/textures/0.jpg", "nodes/1/textures/0_0_1.bin.dds.gz",
            "nodes/1/3dNodeIndexDocument.json.gz", "nodes/1/shared/sharedResource.json.gz");

        Assert.Equal("nodes/1/geometries/0.bin.gz", Entry(package, "nodes/1/geometries/0"));
        Assert.Equal("nodes/1/textures/0.jpg", Entry(package, "nodes/1/textures/0"));
        Assert.Equal("nodes/1/textures/0_0_1.bin.dds.gz", Entry(package, "nodes/1/textures/0_0_1"));
        Assert.Equal("nodes/1/3dnodeindexdocument.json.gz", Entry(package, "nodes/1"));
        Assert.Equal("nodes/1/shared/sharedresource.json.gz", Entry(package, "nodes/1/shared"));
    }

    [Fact]
    public void A_voxel_layers_resources_are_found_below_the_service()
    {
        // The layout of a voxel package ArcGIS Pro wrote (NASA's AIRS_Voxel, read 2026-11-07): served below SceneServer
        // itself, as ArcGIS Online serves a hosted voxel layer (ADR-270 §5.17).
        ScenePackages.Package package = Package(
            "layer.json.gz", "variables/0/index.json.gz", "variables/0/stats.json.gz", "variables/0/0/stats.json.gz",
            "variables/0/0/tree.bin.gz", "variables/0/0/bundles/0/0-0-0.bin.gz");

        Assert.Equal("variables/0/index.json.gz", Entry(package, "variables/0/index"));
        Assert.Equal("variables/0/stats.json.gz", Entry(package, "variables/0/stats"));
        Assert.Equal("variables/0/0/tree.bin.gz", Entry(package, "variables/0/0/tree"));
        Assert.Equal("variables/0/0/bundles/0/0-0-0.bin.gz", Entry(package, "variables/0/0/bundles/0/0-0-0"));
        Assert.Null(Entry(package, "variables/1/index"));
    }
}
