using System;
using System.Text;
using Graticula.Host;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>HTTP Basic on the OGC faces — ADR-178: where it is read, and how a header is read.</summary>
public sealed class BasicCredentialsTests
{
    [Theory]
    [InlineData("/wms", true)]
    [InlineData("/WFS", true)]
    [InlineData("/wcs", true)]
    [InlineData("/wmts", true)]
    [InlineData("/wmts/1.0.0/WMTSCapabilities.xml", true)]
    [InlineData("/ogc/features/v1/collections", true)]
    [InlineData("/ogc/processes/v1/jobs", true)]
    [InlineData("/rest/services/hosted/parcels/MapServer/WMSServer", true)]
    [InlineData("/rest/services/hosted/parcels/FeatureServer/WFSServer", true)]
    [InlineData("/rest/services/hosted/image/ImageServer/WCSServer", true)]
    [InlineData("/rest/services/hosted/image/ImageServer/WMTS/1.0.0/WMTSCapabilities.xml", true)]
    [InlineData("/rest/services", false)]
    [InlineData("/rest/services/hosted/parcels/FeatureServer/0/query", false)]
    [InlineData("/admin/services", false)]
    [InlineData("/sharing/rest/search", false)]
    [InlineData("/wmsx", false)]
    public void Basic_is_read_on_the_ogc_faces_only(string path, bool applies) =>
        Assert.Equal(applies, BasicCredentials.AppliesTo(new PathString(path)));

    [Fact]
    public void A_basic_header_is_read_and_the_password_may_hold_a_colon()
    {
        DefaultHttpContext context = new();
        context.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ayşe:pa:ss"));

        Assert.True(BasicCredentials.TryRead(context, out string name, out string password));
        Assert.Equal("ayşe", name);
        Assert.Equal("pa:ss", password);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer abc")]
    [InlineData("Basic not-base64!")]
    [InlineData("Basic YWxpY2U=")] // "alice", no colon
    [InlineData("Basic YWxpY2U6")] // "alice:", no password
    [InlineData("Basic OnNlY3JldA==")] // ":secret", no name
    public void Anything_else_is_not_a_credential(string? header)
    {
        DefaultHttpContext context = new();

        if (header is not null)
        {
            context.Request.Headers.Authorization = header;
        }

        Assert.False(BasicCredentials.TryRead(context, out _, out _));
    }
}
