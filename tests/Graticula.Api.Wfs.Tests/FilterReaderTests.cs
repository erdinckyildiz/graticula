using System;
using System.Collections.Generic;
using System.Linq;
using Graticula.Features;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Api.Wfs.Tests;

/// <summary>
/// What a Filter Encoding document becomes, and what it is refused for.
/// </summary>
/// <remarks>
/// <b>The refusals are the half worth testing.</b> A filter this server cannot
/// evaluate must produce an error and not a smaller answer — ADR-008 §2 — and
/// the failure mode of getting that wrong is silent: the caller receives features
/// they excluded and nothing anywhere says so.
/// </remarks>
public sealed class FilterReaderTests
{
    private static readonly IReadOnlyList<FieldDescription> Fields =
    [
        new("objectid", FieldType.Integer, Nullable: false, MaxLength: null),
        new("name", FieldType.Text, Nullable: true, MaxLength: 100),
        new("length_m", FieldType.Double, Nullable: true, MaxLength: null),
        new("is_paved", FieldType.Boolean, Nullable: true, MaxLength: null),
    ];

    private static ParsedFilter Ok(string filter, int srid = 3857)
    {
        Assert.True(
            FilterReader.TryRead(filter, Fields, srid, out ParsedFilter parsed, out WfsFault? fault),
            fault?.Text);

        return parsed;
    }

    private static WfsFault Refused(string filter, int srid = 3857)
    {
        Assert.False(
            FilterReader.TryRead(filter, Fields, srid, out _, out WfsFault? fault),
            "the filter was accepted");

        Assert.NotNull(fault);
        return fault!;
    }

    private static string Wrap(string inner) =>
        $"""
         <fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0"
                     xmlns:gml="http://www.opengis.net/gml/3.2">{inner}</fes:Filter>
         """;

    [Theory]
    [InlineData("PropertyIsEqualTo", ComparisonOperator.Equal)]
    [InlineData("PropertyIsNotEqualTo", ComparisonOperator.NotEqual)]
    [InlineData("PropertyIsLessThan", ComparisonOperator.LessThan)]
    [InlineData("PropertyIsGreaterThan", ComparisonOperator.GreaterThan)]
    [InlineData("PropertyIsLessThanOrEqualTo", ComparisonOperator.LessThanOrEqual)]
    [InlineData("PropertyIsGreaterThanOrEqualTo", ComparisonOperator.GreaterThanOrEqual)]
    public void Each_comparison_becomes_its_operator(string element, ComparisonOperator expected)
    {
        ParsedFilter parsed = Ok(Wrap(
            $"<fes:{element}><fes:ValueReference>objectid</fes:ValueReference>"
            + $"<fes:Literal>7</fes:Literal></fes:{element}>"));

        AttributePredicate.Comparison comparison =
            Assert.IsType<AttributePredicate.Comparison>(parsed.Predicate);

        Assert.Equal(expected, comparison.Operator);
        Assert.Equal("objectid", comparison.Column);
    }

    [Fact]
    public void A_literal_takes_the_type_of_the_property_it_is_compared_with()
    {
        // <b>The whole reason the field list is a parameter.</b> Filter Encoding
        // sends every literal as text, so binding "7" as a string against an
        // integer column is an error at the database rather than a comparison.
        Assert.Equal(
            7L,
            Assert.IsType<AttributePredicate.Comparison>(Ok(Wrap(
                "<fes:PropertyIsEqualTo><fes:ValueReference>objectid</fes:ValueReference>"
                + "<fes:Literal>7</fes:Literal></fes:PropertyIsEqualTo>")).Predicate).Value);

        Assert.Equal(
            2.5d,
            Assert.IsType<AttributePredicate.Comparison>(Ok(Wrap(
                "<fes:PropertyIsEqualTo><fes:ValueReference>length_m</fes:ValueReference>"
                + "<fes:Literal>2.5</fes:Literal></fes:PropertyIsEqualTo>")).Predicate).Value);

        Assert.Equal(
            true,
            Assert.IsType<AttributePredicate.Comparison>(Ok(Wrap(
                "<fes:PropertyIsEqualTo><fes:ValueReference>is_paved</fes:ValueReference>"
                + "<fes:Literal>true</fes:Literal></fes:PropertyIsEqualTo>")).Predicate).Value);

        Assert.Equal(
            "Ankara",
            Assert.IsType<AttributePredicate.Comparison>(Ok(Wrap(
                "<fes:PropertyIsEqualTo><fes:ValueReference>name</fes:ValueReference>"
                + "<fes:Literal>Ankara</fes:Literal></fes:PropertyIsEqualTo>")).Predicate).Value);
    }

    [Fact]
    public void A_literal_that_does_not_fit_its_property_is_refused_here_and_not_at_the_database()
    {
        WfsFault fault = Refused(Wrap(
            "<fes:PropertyIsEqualTo><fes:ValueReference>objectid</fes:ValueReference>"
            + "<fes:Literal>not-a-number</fes:Literal></fes:PropertyIsEqualTo>"));

        Assert.Equal(WfsFaultCode.InvalidParameterValue, fault.Code);
        Assert.Contains("whole number", fault.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_property_is_refused_by_name()
    {
        WfsFault fault = Refused(Wrap(
            "<fes:PropertyIsEqualTo><fes:ValueReference>pg_class</fes:ValueReference>"
            + "<fes:Literal>x</fes:Literal></fes:PropertyIsEqualTo>"));

        Assert.Contains("pg_class", fault.Text, StringComparison.Ordinal);
        Assert.Contains("not a property", fault.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_xpath_expression_is_refused_rather_than_approximated()
    {
        // Guessing which property "//*[3]" meant is how a filter silently reads a
        // different column than the caller wrote.
        WfsFault fault = Refused(Wrap(
            "<fes:PropertyIsEqualTo><fes:ValueReference>//*[3]</fes:ValueReference>"
            + "<fes:Literal>x</fes:Literal></fes:PropertyIsEqualTo>"));

        Assert.Equal(WfsFaultCode.OperationNotSupported, fault.Code);
    }

    [Fact]
    public void A_prefixed_property_name_resolves_to_the_column()
    {
        ParsedFilter parsed = Ok(Wrap(
            "<fes:PropertyIsEqualTo><fes:ValueReference>hosted:name</fes:ValueReference>"
            + "<fes:Literal>Ankara</fes:Literal></fes:PropertyIsEqualTo>"));

        Assert.Equal(
            "name", Assert.IsType<AttributePredicate.Comparison>(parsed.Predicate).Column);
    }

    [Fact]
    public void And_joins_an_attribute_test_to_a_spatial_one()
    {
        ParsedFilter parsed = Ok(Wrap("""
            <fes:And>
              <fes:PropertyIsEqualTo>
                <fes:ValueReference>name</fes:ValueReference>
                <fes:Literal>Ankara</fes:Literal>
              </fes:PropertyIsEqualTo>
              <fes:BBOX>
                <fes:ValueReference>geom</fes:ValueReference>
                <gml:Envelope srsName="urn:ogc:def:crs:EPSG::3857">
                  <gml:lowerCorner>0 0</gml:lowerCorner>
                  <gml:upperCorner>10 10</gml:upperCorner>
                </gml:Envelope>
              </fes:BBOX>
            </fes:And>
            """));

        Assert.NotNull(parsed.Predicate);
        Assert.NotNull(parsed.Spatial);
        Assert.Equal(SpatialRelation.EnvelopeIntersects, parsed.Spatial!.Relation);
    }

    [Fact]
    public void Or_across_a_spatial_and_an_attribute_test_is_refused()
    {
        // <b>The refusal this whole design exists for.</b> The query model joins
        // its spatial slot to its predicate with 'and'; applying the attribute half
        // of an 'or' would return a subset, and returning a subset of what was
        // asked for without saying so is the failure ADR-008 §2 names.
        WfsFault fault = Refused(Wrap("""
            <fes:Or>
              <fes:PropertyIsEqualTo>
                <fes:ValueReference>name</fes:ValueReference>
                <fes:Literal>Ankara</fes:Literal>
              </fes:PropertyIsEqualTo>
              <fes:BBOX>
                <gml:Envelope><gml:lowerCorner>0 0</gml:lowerCorner>
                <gml:upperCorner>1 1</gml:upperCorner></gml:Envelope>
              </fes:BBOX>
            </fes:Or>
            """));

        Assert.Equal(WfsFaultCode.OperationNotSupported, fault.Code);
        Assert.Contains("Or", fault.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Not_around_a_spatial_test_is_refused()
    {
        WfsFault fault = Refused(Wrap("""
            <fes:Not>
              <fes:BBOX>
                <gml:Envelope><gml:lowerCorner>0 0</gml:lowerCorner>
                <gml:upperCorner>1 1</gml:upperCorner></gml:Envelope>
              </fes:BBOX>
            </fes:Not>
            """));

        Assert.Equal(WfsFaultCode.OperationNotSupported, fault.Code);
    }

    [Fact]
    public void Two_spatial_predicates_in_one_filter_are_refused()
    {
        WfsFault fault = Refused(Wrap("""
            <fes:And>
              <fes:BBOX><gml:Envelope><gml:lowerCorner>0 0</gml:lowerCorner>
                <gml:upperCorner>1 1</gml:upperCorner></gml:Envelope></fes:BBOX>
              <fes:Intersects><gml:Point><gml:pos>5 5</gml:pos></gml:Point></fes:Intersects>
            </fes:And>
            """));

        Assert.Equal(WfsFaultCode.OperationNotSupported, fault.Code);
        Assert.Contains("Two spatial predicates", fault.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Not_around_an_attribute_test_negates_it()
    {
        ParsedFilter parsed = Ok(Wrap("""
            <fes:Not>
              <fes:PropertyIsEqualTo>
                <fes:ValueReference>name</fes:ValueReference>
                <fes:Literal>Ankara</fes:Literal>
              </fes:PropertyIsEqualTo>
            </fes:Not>
            """));

        Assert.IsType<AttributePredicate.Negation>(parsed.Predicate);
    }

    [Fact]
    public void A_like_pattern_becomes_a_sql_pattern_and_literal_wildcards_are_escaped()
    {
        // <b>Both halves.</b> The client's wildcard becomes SQL's, and a percent
        // the client meant literally is escaped — otherwise a search for "50%"
        // matches everything beginning "50".
        ParsedFilter parsed = Ok(Wrap(
            "<fes:PropertyIsLike wildCard=\"*\" singleChar=\"?\" escapeChar=\"!\">"
            + "<fes:ValueReference>name</fes:ValueReference>"
            + "<fes:Literal>50% An*a?a</fes:Literal></fes:PropertyIsLike>"));

        AttributePredicate.Matches like =
            Assert.IsType<AttributePredicate.Matches>(parsed.Predicate);

        Assert.Equal(@"50\% An%a_a", like.Pattern);
    }

    [Fact]
    public void An_escaped_wildcard_stays_literal()
    {
        ParsedFilter parsed = Ok(Wrap(
            "<fes:PropertyIsLike wildCard=\"*\" singleChar=\"?\" escapeChar=\"!\">"
            + "<fes:ValueReference>name</fes:ValueReference>"
            + "<fes:Literal>a!*b</fes:Literal></fes:PropertyIsLike>"));

        Assert.Equal("a*b", Assert.IsType<AttributePredicate.Matches>(parsed.Predicate).Pattern);
    }

    [Fact]
    public void A_case_insensitive_comparison_is_refused_rather_than_answered_case_sensitively()
    {
        WfsFault fault = Refused(Wrap(
            "<fes:PropertyIsEqualTo matchCase=\"false\">"
            + "<fes:ValueReference>name</fes:ValueReference>"
            + "<fes:Literal>ankara</fes:Literal></fes:PropertyIsEqualTo>"));

        Assert.Equal(WfsFaultCode.OperationNotSupported, fault.Code);
    }

    [Fact]
    public void PropertyIsNull_and_PropertyIsBetween_read()
    {
        Assert.IsType<AttributePredicate.IsNull>(Ok(Wrap(
            "<fes:PropertyIsNull><fes:ValueReference>name</fes:ValueReference>"
            + "</fes:PropertyIsNull>")).Predicate);

        AttributePredicate.Between between = Assert.IsType<AttributePredicate.Between>(Ok(Wrap(
            "<fes:PropertyIsBetween><fes:ValueReference>length_m</fes:ValueReference>"
            + "<fes:LowerBoundary><fes:Literal>1</fes:Literal></fes:LowerBoundary>"
            + "<fes:UpperBoundary><fes:Literal>9</fes:Literal></fes:UpperBoundary>"
            + "</fes:PropertyIsBetween>")).Predicate);

        Assert.Equal(1d, between.Low);
        Assert.Equal(9d, between.High);
    }

    [Fact]
    public void A_resource_id_is_carried_as_an_identity_rather_than_a_predicate()
    {
        ParsedFilter parsed = Ok(Wrap("<fes:ResourceId rid=\"tr_yol.42\"/>"));

        Assert.Equal("tr_yol.42", Assert.Single(parsed.ResourceIds));
        Assert.Null(parsed.Predicate);
    }

    [Fact]
    public void Disjoint_is_refused_by_name_because_the_query_model_cannot_negate_a_relation()
    {
        WfsFault fault = Refused(Wrap(
            "<fes:Disjoint><gml:Point><gml:pos>1 1</gml:pos></gml:Point></fes:Disjoint>"));

        Assert.Equal(WfsFaultCode.OperationNotSupported, fault.Code);
        Assert.Contains("Disjoint", fault.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dwithin_carries_its_distance()
    {
        ParsedFilter parsed = Ok(Wrap("""
            <fes:DWithin>
              <gml:Point><gml:pos>5 5</gml:pos></gml:Point>
              <fes:Distance uom="m">250</fes:Distance>
            </fes:DWithin>
            """));

        Assert.Equal(250d, parsed.Spatial!.Distance);
        Assert.Equal(SpatialRelation.Intersects, parsed.Spatial.Relation);
    }

    [Fact]
    public void A_dwithin_in_units_we_cannot_convert_is_refused()
    {
        WfsFault fault = Refused(Wrap("""
            <fes:DWithin>
              <gml:Point><gml:pos>5 5</gml:pos></gml:Point>
              <fes:Distance uom="mi">3</fes:Distance>
            </fes:DWithin>
            """));

        Assert.Equal(WfsFaultCode.OperationNotSupported, fault.Code);
    }

    [Fact]
    public void A_filter_geometry_in_another_reference_is_reported_rather_than_assumed()
    {
        // <b>Comparing a filter in one reference against data in another is
        // silent</b> — the boxes never meet and the answer is zero features with
        // no error (Q-96). So the reference travels with the geometry.
        ParsedFilter parsed = Ok(Wrap("""
            <fes:Intersects>
              <gml:Point srsName="urn:ogc:def:crs:EPSG::4326"><gml:pos>39.9 32.8</gml:pos></gml:Point>
            </fes:Intersects>
            """));

        Assert.Equal(4326, parsed.FilterSrid);

        Point point = Assert.IsType<Point>(parsed.Spatial!.Geometry);

        // Latitude first in 4326, so 39.9 is the latitude and becomes Y.
        Assert.Equal(32.8, point.X, 6);
        Assert.Equal(39.9, point.Y, 6);
    }

    [Fact]
    public void A_filter_geometry_in_the_layers_own_reference_reports_no_conversion()
    {
        ParsedFilter parsed = Ok(Wrap("""
            <fes:Intersects>
              <gml:Point srsName="urn:ogc:def:crs:EPSG::3857"><gml:pos>10 20</gml:pos></gml:Point>
            </fes:Intersects>
            """));

        Assert.Null(parsed.FilterSrid);

        Point point = Assert.IsType<Point>(parsed.Spatial!.Geometry);
        Assert.Equal(10, point.X, 6);
        Assert.Equal(20, point.Y, 6);
    }

    [Fact]
    public void A_polygon_filter_reads_its_shell_and_holes()
    {
        ParsedFilter parsed = Ok(Wrap("""
            <fes:Within>
              <gml:Polygon srsName="urn:ogc:def:crs:EPSG::3857">
                <gml:exterior><gml:LinearRing>
                  <gml:posList>0 0 10 0 10 10 0 10 0 0</gml:posList>
                </gml:LinearRing></gml:exterior>
                <gml:interior><gml:LinearRing>
                  <gml:posList>2 2 4 2 4 4 2 4 2 2</gml:posList>
                </gml:LinearRing></gml:interior>
              </gml:Polygon>
            </fes:Within>
            """));

        Polygon polygon = Assert.IsType<Polygon>(parsed.Spatial!.Geometry);

        Assert.Equal(5, polygon.Shell.Coordinates.Count);
        Assert.Single(polygon.Holes);
        Assert.Equal(SpatialRelation.Within, parsed.Spatial.Relation);
    }

    [Fact]
    public void An_arc_is_refused_rather_than_read_as_a_straight_line()
    {
        WfsFault fault = Refused(Wrap("""
            <fes:Intersects>
              <gml:Curve><gml:segments><gml:ArcString>
                <gml:posList>0 0 1 1 2 0</gml:posList>
              </gml:ArcString></gml:segments></gml:Curve>
            </fes:Intersects>
            """));

        Assert.Contains("Curve", fault.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_operator_the_capabilities_advertise_is_one_the_reader_accepts()
    {
        // <b>ADR-039's own warning, made mechanical.</b> A capabilities document
        // that advertises an operator the reader refuses produces a client that
        // builds a request from it and reports "the server is broken" rather than
        // "not supported". This walks the advertised list and sends one of each.
        foreach (string name in CapabilitiesDocument.ComparisonOperators)
        {
            string body = name switch
            {
                "PropertyIsLike" =>
                    "<fes:PropertyIsLike wildCard=\"*\" singleChar=\"?\" escapeChar=\"!\">"
                    + "<fes:ValueReference>name</fes:ValueReference>"
                    + "<fes:Literal>a*</fes:Literal></fes:PropertyIsLike>",
                "PropertyIsNull" =>
                    "<fes:PropertyIsNull><fes:ValueReference>name</fes:ValueReference>"
                    + "</fes:PropertyIsNull>",
                "PropertyIsBetween" =>
                    "<fes:PropertyIsBetween><fes:ValueReference>length_m</fes:ValueReference>"
                    + "<fes:LowerBoundary><fes:Literal>1</fes:Literal></fes:LowerBoundary>"
                    + "<fes:UpperBoundary><fes:Literal>2</fes:Literal></fes:UpperBoundary>"
                    + "</fes:PropertyIsBetween>",
                _ => $"<fes:{name}><fes:ValueReference>name</fes:ValueReference>"
                    + $"<fes:Literal>x</fes:Literal></fes:{name}>",
            };

            Assert.True(
                FilterReader.TryRead(Wrap(body), Fields, 3857, out _, out WfsFault? fault),
                $"the capabilities advertise {name} and the reader refused it: {fault?.Text}");
        }

        foreach (string name in CapabilitiesDocument.SpatialOperators)
        {
            string distance = string.Equals(name, "DWithin", StringComparison.Ordinal)
                ? "<fes:Distance>10</fes:Distance>"
                : string.Empty;

            string body =
                $"<fes:{name}><fes:ValueReference>geom</fes:ValueReference>"
                + "<gml:Point><gml:pos>1 1</gml:pos></gml:Point>"
                + distance
                + $"</fes:{name}>";

            Assert.True(
                FilterReader.TryRead(Wrap(body), Fields, 3857, out _, out WfsFault? fault),
                $"the capabilities advertise {name} and the reader refused it: {fault?.Text}");
        }
    }

    [Fact]
    public void Every_geometry_the_capabilities_advertise_is_one_the_reader_accepts()
    {
        Dictionary<string, string> samples = new(StringComparer.Ordinal)
        {
            ["gml:Envelope"] =
                "<gml:Envelope><gml:lowerCorner>0 0</gml:lowerCorner>"
                + "<gml:upperCorner>1 1</gml:upperCorner></gml:Envelope>",
            ["gml:Point"] = "<gml:Point><gml:pos>1 1</gml:pos></gml:Point>",
            ["gml:LineString"] =
                "<gml:LineString><gml:posList>0 0 1 1</gml:posList></gml:LineString>",
            ["gml:Polygon"] =
                "<gml:Polygon><gml:exterior><gml:LinearRing>"
                + "<gml:posList>0 0 1 0 1 1 0 0</gml:posList>"
                + "</gml:LinearRing></gml:exterior></gml:Polygon>",
            ["gml:MultiPoint"] =
                "<gml:MultiPoint><gml:pointMember>"
                + "<gml:Point><gml:pos>1 1</gml:pos></gml:Point>"
                + "</gml:pointMember></gml:MultiPoint>",
            ["gml:MultiCurve"] =
                "<gml:MultiCurve><gml:curveMember>"
                + "<gml:LineString><gml:posList>0 0 1 1</gml:posList></gml:LineString>"
                + "</gml:curveMember></gml:MultiCurve>",
            ["gml:MultiSurface"] =
                "<gml:MultiSurface><gml:surfaceMember><gml:Polygon><gml:exterior>"
                + "<gml:LinearRing><gml:posList>0 0 1 0 1 1 0 0</gml:posList></gml:LinearRing>"
                + "</gml:exterior></gml:Polygon></gml:surfaceMember></gml:MultiSurface>",
        };

        Assert.Equal(
            CapabilitiesDocument.GeometryOperands.OrderBy(n => n, StringComparer.Ordinal),
            samples.Keys.OrderBy(n => n, StringComparer.Ordinal));

        foreach ((string name, string sample) in samples)
        {
            Assert.True(
                FilterReader.TryRead(
                    Wrap($"<fes:Intersects>{sample}</fes:Intersects>"),
                    Fields,
                    3857,
                    out _,
                    out WfsFault? fault),
                $"the capabilities advertise {name} and the reader refused it: {fault?.Text}");
        }
    }

    [Fact]
    public void Nothing_the_caller_wrote_reaches_sql_as_text()
    {
        // The property this whole path exists for, asserted at the far end: the
        // predicate is compiled by the shared emitter and the caller's value is a
        // bound parameter rather than statement text.
        ParsedFilter parsed = Ok(Wrap(
            "<fes:PropertyIsEqualTo><fes:ValueReference>name</fes:ValueReference>"
            + "<fes:Literal>'; drop table parcels --</fes:Literal></fes:PropertyIsEqualTo>"));

        Assert.True(PredicateSql.TryEmit(
            parsed.Predicate,
            [.. Fields.Select(f => f.Name)],
            name => $"\"{name}\"",
            out ParsedWhere emitted,
            out string? error), error);

        Assert.Equal("\"name\" = @w0", emitted.Sql);
        Assert.Equal("'; drop table parcels --", Assert.Single(emitted.Parameters));
    }
}
