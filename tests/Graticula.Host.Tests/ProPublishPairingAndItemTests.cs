using System;
using System.Collections.Generic;
using System.Linq;
using Graticula.Catalog;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-191 section 5.7: what Pro's map and share pane said reaches the service. The dataset in the geodatabase is
/// <c>tur_admin1</c> and the map layer was <i>Provinces by area</i>; the service definition names only the latter, so
/// the pairing of layer to drawing is what carries the symbology, on a publish and on an overwrite. The pane's summary
/// and tags are in the archive's <c>esriinfo/iteminfo.xml</c>, not on the wire.
/// </summary>
public sealed class ProPublishPairingAndItemTests
{
    private static PublishedLayer Layer(string name, string? title = null) =>
        new(Guid.NewGuid(),
            new LayerDefinition(name, "public", name, "geom", 4326, "objectid", "objectid", isHosted: true),
            "postgis", "unused", GeometryKind.Polygon, owner: null, SharingScope.Private, ServiceStatus.Started)
        {
            Title = title,
        };

    private static ServiceDefinition Definition(params string[] mapLayers) =>
        new("Turkey_Provinces", null, null, null, 1, mapLayers.ToDictionary(n => n, _ => "{}"), ItemDetails.None);

    [Fact]
    public void A_paired_layer_is_called_by_its_title_and_addressed_by_its_catalogue_name()
    {
        // ADR-191 5.7: the ArcGIS faces say Pro's name; the layer's catalogue name (the /admin/layers/{name} address) is
        // untouched, and an untitled layer is called what it always was.
        PublishedLayer titled = Layer("tur_admin1", "Provinces by area");

        Assert.Equal("Provinces by area", titled.DisplayName);
        Assert.Equal("tur_admin1", titled.Definition.Name);
        Assert.Equal("tur_admin1", Layer("tur_admin1").DisplayName);
        Assert.Equal("tur_admin1", Layer("tur_admin1", "").DisplayName);
    }

    [Fact]
    public void A_layer_that_was_named_is_still_found_by_the_next_overwrite()
    {
        PublishedLayer named = Layer("tur_admin1", "Provinces by area");

        Assert.Equal("Provinces by area", Assert.Single(
            PortalEndpoints.ProNamed([named, Layer("other")], Definition("Provinces by area", "x"))).Item2);
    }

    [Fact]
    public void A_lone_layer_takes_the_drawing_of_the_lone_map_layer_whatever_they_are_called()
    {
        PublishedLayer dataset = Layer("tur_admin1");

        (PublishedLayer layer, string from) = Assert.Single(
            PortalEndpoints.ProNamed([dataset], Definition("Provinces by area")));

        Assert.Same(dataset, layer);
        Assert.Equal("Provinces by area", from);
    }

    [Fact]
    public void With_several_layers_only_those_whose_names_match_are_paired_with_a_drawing()
    {
        PublishedLayer roads = Layer("roads");
        PublishedLayer rivers = Layer("rivers");

        List<(PublishedLayer, string)> pairs = PortalEndpoints.ProNamed([roads, rivers], Definition("Roads", "Lakes"));

        (PublishedLayer paired, string name) = Assert.Single(pairs);
        Assert.Same(roads, paired);
        Assert.Equal("Roads", name);
    }

    /// <summary>What Pro wrote for the share pane in the 2026-10-18 publish, in the shape its iteminfo.xml has.</summary>
    private const string ItemInfo = """
        <?xml version="1.0" encoding="utf-8" ?>
        <ESRI_ItemInformation Culture='en-US'>
        <name>Turkey_Provinces</name>
        <snippet>Turkey's 81 provinces, coloured by area.</snippet>
        <description>Boundaries from tur_admin1.</description>
        <summary>ignored while there is a snippet</summary>
        <title>Turkey_Provinces</title>
        <tags>Turkey,provinces</tags>
        <type>Service Definition</type>
        </ESRI_ItemInformation>
        """;

    private static ItemDetails Read(string xml) =>
        ServiceDefinitionArchive.ItemOf(new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml.Trim())));

    [Fact]
    public void The_share_panes_summary_description_and_tags_are_read_from_iteminfo()
    {
        ItemDetails item = Read(ItemInfo);

        Assert.Equal("Turkey's 81 provinces, coloured by area.", item.Snippet);
        Assert.Equal("Boundaries from tur_admin1.", item.Description);
        Assert.Equal("Turkey,provinces", item.Tags);
    }

    [Fact]
    public void An_empty_pane_and_an_unreadable_document_say_nothing()
    {
        Assert.Equal(ItemDetails.None, Read("<ESRI_ItemInformation><snippet></snippet><tags/></ESRI_ItemInformation>"));
        Assert.Equal(ItemDetails.None, Read("<ESRI_ItemInformation><snippet>"));
        Assert.Equal(ItemDetails.None, Read("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><ESRI_ItemInformation><snippet>&e;</snippet></ESRI_ItemInformation>"));
    }

    [Fact]
    public void Summary_stands_in_when_there_is_no_snippet()
    {
        Assert.Equal("only this", Read("<ESRI_ItemInformation><summary>only this</summary></ESRI_ItemInformation>").Snippet);
    }
}
