using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Graticula.Features;
using Xunit;

namespace Graticula.Api.Wfs.Tests;

/// <summary>
/// The depth bounds, asserted where an independent reviewer broke them.
/// </summary>
/// <remarks>
/// <para>
/// <b>F-1 of the adversarial review, 2026-08-19, and it took the process down
/// twice.</b> A <c>gml:MultiSurface</c> may hold members that are themselves
/// MultiSurfaces, so <c>GmlGeometryReader</c> recursed with the document. The
/// filter's own depth counter stopped at the boundary and never crossed it. Three
/// thousand levels in a 223 KB unauthenticated POST produced a
/// <c>StackOverflowException</c>, which .NET cannot catch: no refusal, no log
/// line, no server. A thousand levels survived, so the failure was not gradual.
/// </para>
/// <para>
/// <b>The size bounds were never the problem and never helped.</b> One megabyte of
/// characters and four of bytes both held throughout; the crash arrived far below
/// either. A bound on the wrong dimension is a bound that reads as protection.
/// </para>
/// <para>
/// <b>These call the readers directly rather than the server.</b> An assertion
/// about a stack overflow cannot be written as an HTTP test: the process that
/// would report the failure is the process that dies. Falsify by lowering
/// <see cref="SafeXml.MaximumDepth"/> and watching the legitimate cases refuse, or
/// by removing the guard and watching the test host itself disappear.
/// </para>
/// </remarks>
public sealed class NestingTests
{
    private static readonly IReadOnlyList<FieldDescription> Fields =
    [
        new("name", FieldType.Text, Nullable: true, MaxLength: null),
    ];

    private static string NestedCollection(int levels)
    {
        StringBuilder xml = new();

        xml.Append(
            "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\" "
            + "xmlns:gml=\"http://www.opengis.net/gml/3.2\"><fes:Intersects>");

        for (int i = 0; i < levels; i++)
        {
            xml.Append("<gml:MultiSurface><gml:surfaceMember>");
        }

        xml.Append(
            "<gml:Polygon><gml:exterior><gml:LinearRing>"
            + "<gml:posList>0 0 1 0 1 1 0 0</gml:posList>"
            + "</gml:LinearRing></gml:exterior></gml:Polygon>");

        for (int i = 0; i < levels; i++)
        {
            xml.Append("</gml:surfaceMember></gml:MultiSurface>");
        }

        xml.Append("</fes:Intersects></fes:Filter>");

        return xml.ToString();
    }

    [Fact]
    public void A_geometry_nested_past_the_limit_is_refused_rather_than_overflowing()
    {
        // The reviewer's shape, an order of magnitude smaller. If the guard is
        // absent this does not fail; it takes the test host with it.
        Assert.False(
            FilterReader.TryRead(NestedCollection(300), Fields, 4326, out _, out WfsFault? fault),
            "a 300-level geometry was accepted");

        Assert.NotNull(fault);
        Assert.Contains("nests more than", fault!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reviewers_own_depth_is_refused()
    {
        // Three thousand levels is what actually killed the server. Kept because a
        // limit that holds at 300 and not at 3,000 would be a limit that scales
        // with luck.
        Assert.False(
            FilterReader.TryRead(NestedCollection(3_000), Fields, 4326, out _, out WfsFault? fault),
            "the reviewer's own payload was accepted");

        Assert.NotNull(fault);
    }

    [Fact]
    public void A_real_multipolygon_is_still_read()
    {
        // <b>The control, and the first version of it was wrong in a way worth
        // keeping.</b> It nested MultiSurfaces five deep and expected success —
        // but a MultiSurface holding a MultiSurface was never valid here anyway:
        // the parts must be polygons, so the cast fails and the filter is refused.
        // Nesting was never a legitimate shape; it was only ever a way to recurse.
        // That is why the crash was reachable through input this server would have
        // rejected a moment later, and why a bound on validity would not have
        // helped. What a real client sends is this: one collection, several
        // polygons.
        string filter =
            "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\" "
            + "xmlns:gml=\"http://www.opengis.net/gml/3.2\"><fes:Intersects>"
            + "<gml:MultiSurface>"
            + "<gml:surfaceMember><gml:Polygon><gml:exterior><gml:LinearRing>"
            + "<gml:posList>0 0 1 0 1 1 0 0</gml:posList>"
            + "</gml:LinearRing></gml:exterior></gml:Polygon></gml:surfaceMember>"
            + "<gml:surfaceMember><gml:Polygon><gml:exterior><gml:LinearRing>"
            + "<gml:posList>5 5 6 5 6 6 5 5</gml:posList>"
            + "</gml:LinearRing></gml:exterior></gml:Polygon></gml:surfaceMember>"
            + "</gml:MultiSurface></fes:Intersects></fes:Filter>";

        Assert.True(
            FilterReader.TryRead(filter, Fields, 4326, out ParsedFilter parsed, out WfsFault? fault),
            fault?.Text);

        Assert.NotNull(parsed.Spatial);
    }

    [Fact]
    public void The_budget_is_shared_between_the_filter_and_its_geometry()
    {
        // <b>Two counters that each allow the maximum allow twice it.</b> The
        // predicates around a geometry are the same stack as the geometry, so the
        // filter's depth is handed across the boundary rather than restarted.
        StringBuilder xml = new();

        xml.Append(
            "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\" "
            + "xmlns:gml=\"http://www.opengis.net/gml/3.2\">");

        int wrappers = SafeXml.MaximumDepth - 4;

        for (int i = 0; i < wrappers; i++)
        {
            xml.Append("<fes:And><fes:PropertyIsNull><fes:ValueReference>name</fes:ValueReference>"
                + "</fes:PropertyIsNull>");
        }

        xml.Append("<fes:Intersects>");

        for (int i = 0; i < 10; i++)
        {
            xml.Append("<gml:MultiSurface><gml:surfaceMember>");
        }

        xml.Append(
            "<gml:Polygon><gml:exterior><gml:LinearRing>"
            + "<gml:posList>0 0 1 0 1 1 0 0</gml:posList>"
            + "</gml:LinearRing></gml:exterior></gml:Polygon>");

        for (int i = 0; i < 10; i++)
        {
            xml.Append("</gml:surfaceMember></gml:MultiSurface>");
        }

        xml.Append("</fes:Intersects>");

        for (int i = 0; i < wrappers; i++)
        {
            xml.Append("</fes:And>");
        }

        xml.Append("</fes:Filter>");

        // Neither half is past the limit on its own. Together they are, and that is
        // the arithmetic a per-reader counter gets wrong.
        Assert.False(
            FilterReader.TryRead(xml.ToString(), Fields, 4326, out _, out WfsFault? fault),
            "the filter's depth was not carried into its geometry");

        Assert.NotNull(fault);
    }

    [Fact]
    public void The_operation_comes_from_the_element_and_not_from_an_attribute()
    {
        // F-2 of the same review. A `request` attribute overwrote the operation the
        // root element names, so a GetFeature document could answer capabilities.
        // Nothing crossed an authorization boundary — every operation here is
        // anonymous — but the two encodings disagreed, and the KVP form cannot
        // express the same confusion.
        string body =
            "<wfs:GetFeature service=\"WFS\" version=\"2.0.0\" request=\"GetCapabilities\""
            + " xmlns:wfs=\"http://www.opengis.net/wfs/2.0\">"
            + "<wfs:Query typeNames=\"graticula:tr_il\"/>"
            + "</wfs:GetFeature>";

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(body));

        Assert.True(WfsXmlRequest.TryRead(
            stream, out IReadOnlyDictionary<string, string> parameters, out _));

        Assert.True(WfsRequest.TryParse(parameters, out WfsRequest? request, out WfsFault? fault),
            fault?.Text);

        Assert.Equal(WfsOperation.GetFeature, request!.Operation);
    }
}
