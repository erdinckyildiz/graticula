using System;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Host;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A MapServer export's bbox with no <c>bboxSR</c>, read in the reference the map states.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-229](../../docs/architecture-debt.md)'s last face.</b> ArcGIS's published Export Map
/// reference says that a bbox given without <c>bboxSR</c> <em>is assumed to be in the spatial
/// reference of the map</em>. This parser assumed <b>4326</b> whatever the map said, so the
/// MapServer document stated the table's reference — 3857, say — while the export read the
/// same unqualified numbers as degrees. A client that took the document at its word and sent a
/// box in map units got an image of a different place, with a 200 and nothing to say why.
/// </para>
/// <para>
/// <b>And when a service names its own reference, the map is in that one</b> — owner decision
/// 2026-09-11, that MapServer follows the service's choice as every other face already did. So
/// the default is the layer's <c>PublishedSrid</c>: the service's reference when it chose one,
/// the table's when it did not.
/// </para>
/// </remarks>
public sealed class AnUnqualifiedExportBoxIsTheMapsReferenceTests
{
    private static PublishedLayer Layer(int tableSrid, int? served) =>
        new(
            Guid.NewGuid(),
            new LayerDefinition("parcels", "public", "parcels", "geom", tableSrid, "id", "id", isHosted: false),
            "map",
            "Host=nowhere",
            GeometryKind.Polygon,
            owner: null,
            SharingScope.Public,
            ServiceStatus.Started,
            servedSrid: served);

    private static MapServerExportParameters Parse(PublishedLayer layer, string? bboxSr)
    {
        string? Parameter(string name) => name switch
        {
            "bbox" => "3000000,4900000,3100000,5000000",
            "bboxSR" => bboxSr,
            "size" => "256,256",
            "format" => "png",
            _ => null,
        };

        Assert.True(
            MapServerExportParameters.TryParse(
                Parameter, [layer], new WidthHeight(4096, 4096), out MapServerExportParameters? parsed,
                out string? error),
            error);

        return parsed!;
    }

    [Fact]
    public void With_no_bboxSR_the_box_is_in_the_tables_reference_when_the_service_chose_none()
    {
        // The case the old 4326 default got wrong for every service that exists: a 3857 table,
        // no reference chosen, a box in metres. Reading it as degrees would have drawn nowhere.
        Assert.Equal(3857, Parse(Layer(3857, served: null), bboxSr: null).ImageSrid);
    }

    [Fact]
    public void With_no_bboxSR_the_box_is_in_the_services_reference_when_it_chose_one()
    {
        Assert.Equal(5253, Parse(Layer(3857, served: 5253), bboxSr: null).ImageSrid);
    }

    [Fact]
    public void A_bboxSR_the_caller_gives_still_wins()
    {
        // The default is for the caller who said nothing. One who said something is obeyed.
        Assert.Equal(4326, Parse(Layer(3857, served: 5253), bboxSr: "4326").ImageSrid);
    }
}
