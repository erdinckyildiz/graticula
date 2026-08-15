using System.Collections.Generic;
using GisServer.Host;
using Xunit;

namespace GisServer.Host.Tests;

/// <summary>
/// The browsable directory, and mostly the encoding of it.
/// </summary>
/// <remarks>
/// <b>These are XSS tests wearing a rendering costume.</b> Every name in this
/// directory is user input — a layer name, a folder, a field alias — and the
/// person reading the page is a GIS administrator holding every privilege the
/// server has. A name rendered raw is stored XSS against exactly the account an
/// attacker would want. The tests below check the output, not the helper, so
/// that adding a new row to the page without encoding it fails here.
/// </remarks>
public sealed class RestDirectoryTests
{
    [Theory]
    [InlineData("html", null, true)]
    [InlineData("HTML", null, true)]
    [InlineData("json", "text/html", false)]
    [InlineData("pjson", "text/html", false)]
    [InlineData(null, "text/html,application/xhtml+xml", true)]
    [InlineData(null, "application/json", false)]
    [InlineData(null, null, false)]
    [InlineData("", null, false)]
    public void FormatDecidesBeforeAccept(string? format, string? accept, bool html)
    {
        // An explicit f always wins — otherwise a browser could never ask for
        // JSON, and ?f=json is how every existing caller in the test suite works.
        Assert.Equal(html, RestDirectory.WantsHtml(format, accept));
    }

    [Fact]
    public void ServiceNameIsEncoded()
    {
        string page = RestDirectory.Folder(
            "/rest/services",
            folder: null,
            version: 10.81,
            folders: [],
            services: [("<script>alert(1)</script>", "FeatureServer")]);

        Assert.DoesNotContain("<script>", page, System.StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public void FolderNameIsEncoded()
    {
        string page = RestDirectory.Folder(
            "/rest/services",
            folder: "\"><img src=x onerror=alert(1)>",
            version: 10.81,
            folders: ["<b>bold</b>"],
            services: []);

        Assert.DoesNotContain("<img", page, System.StringComparison.Ordinal);
        Assert.DoesNotContain("<b>bold", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public void AttributeValuesCannotBreakOutOfTheHref()
    {
        // The name reaches an href as well as a text node, and the two need
        // different escapes. A quote surviving into the attribute is enough.
        string page = RestDirectory.Folder(
            "/rest/services",
            folder: null,
            version: 10.81,
            folders: [],
            services: [("a\" onmouseover=\"alert(1)", "FeatureServer")]);

        Assert.DoesNotContain("onmouseover=\"alert", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public void FolderQualifiedNamesKeepTheirSlash()
    {
        // Escaping the whole name turned Utilities/Geometry into
        // Utilities%2FGeometry and the link 404'd. The slash is structure.
        string page = RestDirectory.Folder(
            "/rest/services/Utilities",
            folder: "Utilities",
            version: 10.81,
            folders: [],
            services: [("Utilities/Geometry", "GeometryServer")]);

        Assert.Contains(
            "href=\"/rest/services/Utilities/Geometry/GeometryServer\"",
            page,
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void ValuesInADocumentAreEncoded()
    {
        string page = RestDirectory.Document(
            "/rest/services/x/FeatureServer/0",
            "x",
            new
            {
                name = "<script>alert(1)</script>",
                fields = new[] { new { alias = "<i>a</i>" } },
            });

        Assert.DoesNotContain("<script>", page, System.StringComparison.Ordinal);
        Assert.DoesNotContain("<i>a</i>", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public void PropertyNamesBecomeReadable()
    {
        string page = RestDirectory.Document(
            "/rest/services/x/FeatureServer/0", "x", new { maxRecordCount = 50000 });

        Assert.Contains("Max Record Count", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public void EmptinessIsStatedRatherThanLeftBlank()
    {
        // An empty list and a list that failed to load look identical, and here
        // the first is a fact worth saying: sharing may be the reason.
        string page = RestDirectory.Folder(
            "/rest/services", folder: null, version: 10.81, folders: [], services: []);

        Assert.Contains("No services", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public void BreadcrumbJoinsTheServiceToItsType()
    {
        // There is no resource at .../parcels, only at .../parcels/FeatureServer,
        // so a crumb per segment produces a link that 404s.
        string page = RestDirectory.Document(
            "/rest/services/hosted/parcels/FeatureServer/0", "parcels", new { id = 0 });

        Assert.Contains(
            "<a href=\"/rest/services/hosted/parcels/FeatureServer\">parcels (FeatureServer)</a>",
            page,
            System.StringComparison.Ordinal);

        Assert.DoesNotContain(
            "<a href=\"/rest/services/hosted/parcels\">", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPageOffersTheJsonItRenders()
    {
        // The directory is a view of the API, and somebody reading it is usually
        // about to write a client against the JSON behind it.
        string page = RestDirectory.Folder(
            "/rest/services/hosted", "hosted", 10.81, [], new List<(string, string)>());

        Assert.Contains("/rest/services/hosted?f=json", page, System.StringComparison.Ordinal);
    }
}
