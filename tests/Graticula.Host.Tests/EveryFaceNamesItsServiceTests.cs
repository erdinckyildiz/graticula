using Graticula.Host;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The request log names the service on every face that serves one.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-255](../../docs/architecture-debt.md), found by walking
/// [ADR-061](../../docs/adr/ADR-061-metrics-are-aggregate-and-per-entity-numbers-live-in-the-admin-api.md)
/// condition 2.</b> That condition asks whether the 2 AM scenario — <i>this service is slow</i>
/// — names the service, using only what ADR-007 §5 leaves available. It turned on a column:
/// <c>request_log.service</c> exists, and three of the four serving faces were writing null into
/// it.
/// </para>
/// <para>
/// <b>Measured on the fixture before this was written</b>: ArcGIS 219,461 rows and 64.3% named;
/// OGC 30,678, WMS 9,848 and WFS 5,674 rows, and <b>0%</b> named on each. The store could answer
/// the question with one group-by for one face and could not answer it at all for the other
/// three.
/// </para>
/// <para>
/// <b>A unit test, because the derivation is a pure function of a path and a query.</b> The
/// behaviour over HTTP is what the log then holds, and that is asserted where the log is read;
/// this pins the rule itself, cheaply, from every direction including the shapes that must stay
/// null.
/// </para>
/// </remarks>
public sealed class EveryFaceNamesItsServiceTests
{
    /// <summary>Each face names what it served, where that face puts it.</summary>
    /// <param name="path">The request path.</param>
    /// <param name="query">Its query string.</param>
    /// <param name="expected">The service the log should file it under.</param>
    [Theory]
    // ArcGIS: in the path, with its folder.
    [InlineData("/rest/services/hosted/ci_many/FeatureServer/0/query", "", "hosted/ci_many")]
    [InlineData("/rest/services/ci_many/FeatureServer", "", "ci_many")]

    // OGC API Features: the collection is a path segment, on every route under it.
    [InlineData("/ogc/features/v1/collections/ci_buildings/items", "?limit=1", "ci_buildings")]
    [InlineData("/ogc/features/v1/collections/ci_editable", "", "ci_editable")]
    [InlineData("/ogc/features/v1/collections/ci_many/items/7", "", "ci_many")]

    // WMS and WFS: the protocol puts it in the query, and the names are case-insensitive.
    [InlineData("/wms", "?request=GetMap&layers=ci_buildings&crs=EPSG:3857", "ci_buildings")]
    [InlineData("/wms", "?REQUEST=GetFeatureInfo&LAYERS=ci_many", "ci_many")]
    [InlineData("/wfs", "?request=GetFeature&typeNames=ci_parcels", "ci_parcels")]
    [InlineData("/wfs", "?request=DescribeFeatureType&TYPENAMES=ci_many", "ci_many")]

    // <b>The first of a list, which is a choice rather than a truth.</b> A GetMap may name
    // several layers and a log column holds one; the rule is written down so nobody reads a
    // single-service row as proof the request touched one.
    [InlineData("/wms", "?layers=ci_buildings,ci_many", "ci_buildings")]
    public void A_request_is_filed_under_what_it_named(string path, string query, string expected) =>
        Assert.Equal(
            expected,
            RequestFacts.Service(new PathString(path), new QueryString(query)));

    /// <summary>What names no service is filed under none, rather than under something wrong.</summary>
    /// <param name="path">The request path.</param>
    /// <param name="query">Its query string.</param>
    /// <remarks>
    /// <b>The half a repair breaks.</b> Reading a query parameter to find a service invites
    /// filing a capabilities document under whatever happened to be in the query, and filing
    /// the console's own assets under a path segment that looks like a name. Both are asserted
    /// because both would look like the feature working.
    /// </remarks>
    [Theory]
    [InlineData("/wms", "?service=WMS&version=1.3.0&request=GetCapabilities")]
    [InlineData("/wfs", "?service=WFS&version=2.0.0&request=GetCapabilities")]
    [InlineData("/ogc/features/v1/collections", "")]
    [InlineData("/ogc/features/v1/conformance", "")]
    [InlineData("/admin/logs/requests", "?limit=60")]
    [InlineData("/server/console.js", "")]
    [InlineData("/rest/info", "")]
    [InlineData("/wms", "?layers=")]
    public void A_request_that_named_nothing_is_filed_under_nothing(string path, string query) =>
        Assert.Null(RequestFacts.Service(new PathString(path), new QueryString(query)));

    /// <summary>The path-only overload still answers as it did, for the callers that use it.</summary>
    [Fact]
    public void The_path_only_reading_is_unchanged() =>
        Assert.Equal(
            "hosted/ci_many",
            RequestFacts.Service(new PathString("/rest/services/hosted/ci_many/MapServer")));
}
