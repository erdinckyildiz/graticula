using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace Graticula.Api.Wfs.Tests;

/// <summary>Binding a request, and the refusals that protect a client from a wrong answer.</summary>
public sealed class WfsRequestTests
{
    private static WfsRequest Ok(params (string Key, string Value)[] parameters)
    {
        Dictionary<string, string> kvp = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string key, string value) in parameters)
        {
            kvp[key] = value;
        }

        Assert.True(WfsRequest.TryParse(kvp, out WfsRequest? request, out WfsFault? fault),
            fault?.Text);

        return request!;
    }

    private static WfsFault Refused(params (string Key, string Value)[] parameters)
    {
        Dictionary<string, string> kvp = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string key, string value) in parameters)
        {
            kvp[key] = value;
        }

        Assert.False(WfsRequest.TryParse(kvp, out _, out WfsFault? fault), "it bound");
        Assert.NotNull(fault);
        return fault!;
    }

    [Fact]
    public void Parameter_names_are_matched_however_the_client_capitalises_them()
    {
        // Three spellings of the same request, because all three arrive.
        Assert.Equal(WfsOperation.GetFeature, Ok(
            ("SERVICE", "WFS"), ("VERSION", "2.0.0"), ("REQUEST", "GetFeature"),
            ("TYPENAMES", "hosted:roads")).Operation);

        Assert.Equal("hosted:roads", Assert.Single(Ok(
            ("service", "wfs"), ("version", "2.0.0"), ("request", "getfeature"),
            ("typeNames", "hosted:roads")).TypeNames));

        Assert.Equal("hosted:roads", Assert.Single(Ok(
            ("Service", "WFS"), ("Version", "2.0.0"), ("Request", "GetFeature"),
            ("typename", "hosted:roads")).TypeNames));
    }

    [Fact]
    public void A_client_asking_for_an_earlier_version_is_told_rather_than_answered()
    {
        // <b>ADR-039 §5.</b> A 2.0.0 document returned for a 1.1.0 request is
        // indistinguishable from a server that is simply wrong, and the client has
        // no way to find out which.
        WfsFault fault = Refused(
            ("service", "WFS"), ("version", "1.1.0"), ("request", "GetFeature"),
            ("typeNames", "roads"));

        Assert.Equal(WfsFaultCode.VersionNegotiationFailed, fault.Code);
        Assert.Contains("2.0.0", fault.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Capabilities_negotiates_and_everything_else_asserts()
    {
        // Discovery is allowed to ask; an operation is not.
        Assert.Equal(
            WfsOperation.GetCapabilities,
            Ok(("service", "WFS"), ("request", "GetCapabilities")).Operation);

        Assert.Equal(
            WfsOperation.GetCapabilities,
            Ok(("service", "WFS"), ("request", "GetCapabilities"),
                ("acceptversions", "2.0.0,1.1.0")).Operation);

        Assert.Equal(
            WfsFaultCode.VersionNegotiationFailed,
            Refused(("service", "WFS"), ("request", "GetCapabilities"),
                ("acceptversions", "1.0.0,1.1.0")).Code);

        Assert.Equal(
            WfsFaultCode.MissingParameterValue,
            Refused(("service", "WFS"), ("request", "GetFeature"), ("typeNames", "roads")).Code);
    }

    [Fact]
    public void An_operation_this_server_does_not_implement_says_so_by_name()
    {
        WfsFault fault = Refused(
            ("service", "WFS"), ("version", "2.0.0"), ("request", "Transaction"));

        Assert.Equal(WfsFaultCode.OperationNotSupported, fault.Code);
        Assert.Contains("Transaction", fault.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("application/gml+xml; version=3.2", WfsOutputFormat.Gml)]
    [InlineData("application/gml+xml;version=3.2", WfsOutputFormat.Gml)]
    [InlineData("GML32", WfsOutputFormat.Gml)]
    [InlineData("text/xml; subtype=gml/3.2", WfsOutputFormat.Gml)]
    [InlineData("text/xml", WfsOutputFormat.Gml)]
    [InlineData("application/json", WfsOutputFormat.GeoJson)]
    [InlineData("application/geo+json", WfsOutputFormat.GeoJson)]
    [InlineData("json", WfsOutputFormat.GeoJson)]
    public void The_output_format_is_recognised_however_it_is_punctuated(
        string text, WfsOutputFormat expected)
    {
        Assert.Equal(expected, Ok(
            ("service", "WFS"), ("version", "2.0.0"), ("request", "GetFeature"),
            ("typeNames", "roads"), ("outputFormat", text)).Format);
    }

    [Fact]
    public void The_default_output_format_is_gml_because_that_is_what_wfs_says()
    {
        Assert.Equal(WfsOutputFormat.Gml, Ok(
            ("service", "WFS"), ("version", "2.0.0"), ("request", "GetFeature"),
            ("typeNames", "roads")).Format);
    }

    [Fact]
    public void A_format_this_server_does_not_write_names_the_ones_it_does()
    {
        WfsFault fault = Refused(
            ("service", "WFS"), ("version", "2.0.0"), ("request", "GetFeature"),
            ("typeNames", "roads"), ("outputFormat", "shape-zip"));

        Assert.Contains("gml", fault.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("json", fault.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Paging_and_hits_bind()
    {
        WfsRequest request = Ok(
            ("service", "WFS"), ("version", "2.0.0"), ("request", "GetFeature"),
            ("typeNames", "roads"), ("count", "25"), ("startIndex", "50"),
            ("resultType", "hits"));

        Assert.Equal(25, request.Count);
        Assert.Equal(50, request.StartIndex);
        Assert.True(request.HitsOnly);
    }

    [Fact]
    public void MaxFeatures_is_accepted_as_counts_older_spelling()
    {
        Assert.Equal(10, Ok(
            ("service", "WFS"), ("version", "2.0.0"), ("request", "GetFeature"),
            ("typeNames", "roads"), ("maxFeatures", "10")).Count);
    }

    [Fact]
    public void A_negative_count_is_refused()
    {
        Assert.Equal(
            WfsFaultCode.InvalidParameterValue,
            Refused(
                ("service", "WFS"), ("version", "2.0.0"), ("request", "GetFeature"),
                ("typeNames", "roads"), ("count", "-1")).Code);
    }

    [Fact]
    public void A_result_type_that_is_neither_is_refused()
    {
        Assert.Equal(
            WfsFaultCode.InvalidParameterValue,
            Refused(
                ("service", "WFS"), ("version", "2.0.0"), ("request", "GetFeature"),
                ("typeNames", "roads"), ("resultType", "maybe")).Code);
    }

    [Fact]
    public void A_prefix_is_whatever_the_request_says_it_is()
    {
        // <b>The defect the OGC conformance suite found and nothing else could.</b>
        // WFS 2.0 §7.9.2 lets a request bind its own prefixes and then use them, so
        // this asks for exactly what `hosted:look_parcels` asks for. Every client
        // that reads the capabilities first uses the prefixes it saw there, so a
        // server that treats the prefix as a name works with all of them and fails
        // the specification.
        WfsRequest request = Ok(
            ("service", "WFS"), ("version", "2.0.0"), ("request", "GetFeature"),
            ("typenames", "ns98:look_parcels"),
            ("namespaces",
             "xmlns(xml,http://www.w3.org/XML/1998/namespace),"
             + "xmlns(ns98,urn:graticula:ns),"
             + "xmlns(wfs,http://www.opengis.net/wfs/2.0)"));

        Assert.Equal(WfsNames.Namespace, request.Namespaces["ns98"]);
        Assert.Equal(WfsNames.Namespace, request.Namespaces["ns98"]);
        Assert.Equal("http://www.opengis.net/wfs/2.0", request.Namespaces["wfs"]);
    }

    [Fact]
    public void The_default_namespace_form_binds_the_empty_prefix()
    {
        WfsRequest request = Ok(
            ("service", "WFS"), ("version", "2.0.0"), ("request", "GetFeature"),
            ("typenames", "roads"),
            ("namespaces", "xmlns(urn:graticula:ns)"));

        Assert.Equal(WfsNames.Namespace, request.Namespaces[string.Empty]);
    }

    [Fact]
    public void A_bound_prefix_only_means_something_if_it_names_our_namespace()
    {
        // One namespace, so the check is an equality rather than a lookup. The
        // point survives the simplification: the prefix is resolved, never read.
        Assert.Equal("urn:graticula:ns", WfsNames.Namespace);

        WfsRequest request = Ok(
            ("service", "WFS"), ("version", "2.0.0"), ("request", "GetFeature"),
            ("typenames", "zz:tr_il"),
            ("namespaces", "xmlns(zz,http://example.org/other)"));

        Assert.Equal("http://example.org/other", request.Namespaces["zz"]);
    }

    [Fact]
    public void An_xml_request_carries_its_namespace_declarations_across()
    {
        // The POST path reduces to the KVP path, so the binding has to survive the
        // reduction or the same defect returns on the other encoding.
        string body =
            "<wfs:GetFeature service=\"WFS\" version=\"2.0.0\""
            + " xmlns:wfs=\"http://www.opengis.net/wfs/2.0\""
            + " xmlns:ns7=\"urn:graticula:ns\">"
            + "<wfs:Query typeNames=\"ns7:look_parcels\"/>"
            + "</wfs:GetFeature>";

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(body));

        Assert.True(WfsXmlRequest.TryRead(
            stream, out IReadOnlyDictionary<string, string> parameters, out _));

        Assert.True(WfsRequest.TryParse(parameters, out WfsRequest? request, out WfsFault? fault),
            fault?.Text);

        Assert.Equal(WfsNames.Namespace, request!.Namespaces["ns7"]);
        Assert.Equal("ns7:look_parcels", Assert.Single(request.TypeNames));
    }

    [Fact]
    public void An_xml_request_binds_to_the_same_shape_as_the_query_string()
    {
        // <b>One binder, two encodings.</b> If these drifted, a client that
        // switched to POST for a long filter would meet a different server.
        const string Body = """
            <wfs:GetFeature service="WFS" version="2.0.0" count="5" startIndex="10"
                            outputFormat="application/json" resultType="hits"
                            xmlns:wfs="http://www.opengis.net/wfs/2.0"
                            xmlns:fes="http://www.opengis.net/fes/2.0">
              <wfs:Query typeNames="hosted:roads" srsName="urn:ogc:def:crs:EPSG::4326">
                <fes:Filter>
                  <fes:PropertyIsEqualTo>
                    <fes:ValueReference>name</fes:ValueReference>
                    <fes:Literal>Ankara</fes:Literal>
                  </fes:PropertyIsEqualTo>
                </fes:Filter>
                <fes:SortBy>
                  <fes:SortProperty>
                    <fes:ValueReference>name</fes:ValueReference>
                    <fes:SortOrder>DESC</fes:SortOrder>
                  </fes:SortProperty>
                </fes:SortBy>
              </wfs:Query>
            </wfs:GetFeature>
            """;

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(Body));

        Assert.True(
            WfsXmlRequest.TryRead(
                stream, out IReadOnlyDictionary<string, string> parameters, out WfsFault? read),
            read?.Text);

        Assert.True(
            WfsRequest.TryParse(parameters, out WfsRequest? request, out WfsFault? fault),
            fault?.Text);

        Assert.Equal(WfsOperation.GetFeature, request!.Operation);
        Assert.Equal("hosted:roads", Assert.Single(request.TypeNames));
        Assert.Equal(WfsOutputFormat.GeoJson, request.Format);
        Assert.Equal(5, request.Count);
        Assert.Equal(10, request.StartIndex);
        Assert.Equal(4326, request.Srid);
        Assert.True(request.HitsOnly);
        Assert.Equal("name DESC", Assert.Single(request.SortBy));
        Assert.Contains("Ankara", request.Filter!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_xml_stored_query_binds_as_a_get_feature()
    {
        const string Body = """
            <wfs:GetFeature service="WFS" version="2.0.0"
                            xmlns:wfs="http://www.opengis.net/wfs/2.0">
              <wfs:StoredQuery id="urn:ogc:def:query:OGC-WFS::GetFeatureById">
                <wfs:Parameter name="id">roads.42</wfs:Parameter>
              </wfs:StoredQuery>
            </wfs:GetFeature>
            """;

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(Body));

        Assert.True(WfsXmlRequest.TryRead(
            stream, out IReadOnlyDictionary<string, string> parameters, out _));

        Assert.True(WfsRequest.TryParse(parameters, out WfsRequest? request, out WfsFault? fault),
            fault?.Text);

        Assert.Equal(WfsOperation.GetFeature, request!.Operation);
        Assert.Equal(WfsRequest.GetFeatureByIdQuery, request.StoredQueryId);
        Assert.Equal("roads.42", Assert.Single(request.ResourceIds));
    }

    [Fact]
    public void An_xml_describe_binds_its_type_names_from_elements()
    {
        const string Body = """
            <wfs:DescribeFeatureType service="WFS" version="2.0.0"
                                     xmlns:wfs="http://www.opengis.net/wfs/2.0">
              <wfs:TypeName>hosted:roads</wfs:TypeName>
              <wfs:TypeName>hosted:rivers</wfs:TypeName>
            </wfs:DescribeFeatureType>
            """;

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(Body));

        Assert.True(WfsXmlRequest.TryRead(
            stream, out IReadOnlyDictionary<string, string> parameters, out _));

        Assert.True(WfsRequest.TryParse(parameters, out WfsRequest? request, out _));

        Assert.Equal(WfsOperation.DescribeFeatureType, request!.Operation);
        Assert.Equal(["hosted:roads", "hosted:rivers"], request.TypeNames);
    }
}
