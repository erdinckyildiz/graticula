using System.Collections.Generic;
using Graticula.Host;
using Xunit;

namespace Graticula.Host.Tests;

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

    // ---------- the layer tree ----------

    /// <summary>
    /// A child list lives inside its parent's list item, not beside it.
    /// </summary>
    /// <remarks>
    /// <b>Written after getting it wrong.</b> The first version emitted the
    /// nested <c>&lt;ul&gt;</c> as a sibling of the <c>&lt;li&gt;</c> it belonged
    /// to. Browsers indent that close enough to correct that looking at the page
    /// does not catch it — but the document is invalid and a screen reader reads
    /// the tree flat, which is exactly the information a group layer exists to
    /// carry.
    /// </remarks>
    [Fact]
    public void A_nested_layer_list_is_inside_its_parent_item()
    {
        string page = RestDirectory.Document(
            "/rest/services/x/FeatureServer",
            "x",
            new { id = 0 },
            tree:
            [
                ("Group (0)", "/x/0", 0),
                ("Child (1)", "/x/1", 1),
            ]);

        Assert.Contains(
            "<li><a href=\"/x/0\">Group (0)</a><ul><li><a href=\"/x/1\">Child (1)</a></li></ul></li>",
            page,
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void Every_opened_list_and_item_is_closed()
    {
        // Counted rather than eyeballed. An unclosed <ul> swallows the rest of
        // the page into the tree, and the page still looks plausible.
        string page = RestDirectory.Document(
            "/rest/services/x/FeatureServer",
            "x",
            new { id = 0 },
            tree:
            [
                ("Group (0)", "/x/0", 0),
                ("A (1)", "/x/1", 1),
                ("Inner (2)", "/x/2", 1),
                ("Deep (3)", "/x/3", 2),
                ("Top (4)", "/x/4", 0),
            ]);

        Assert.Equal(Count(page, "<ul>"), Count(page, "</ul>"));
        Assert.Equal(Count(page, "<li>"), Count(page, "</li>"));
    }

    [Fact]
    public void A_branch_that_ends_several_levels_deep_closes_all_of_them()
    {
        // Depth steps up by one at a time and down by several, which is why
        // closing is a loop and opening is not.
        string page = RestDirectory.Document(
            "/rest/services/x/FeatureServer",
            "x",
            new { id = 0 },
            tree:
            [
                ("A (0)", "/x/0", 0),
                ("B (1)", "/x/1", 1),
                ("C (2)", "/x/2", 2),
                ("D (3)", "/x/3", 0),
            ]);

        Assert.Equal(Count(page, "<ul>"), Count(page, "</ul>"));
        Assert.Contains("</ul></li></ul></li><li><a href=\"/x/3\">", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_service_with_no_layers_says_so_in_the_tree()
    {
        string page = RestDirectory.Document(
            "/rest/services/x/FeatureServer", "x", new { id = 0 }, tree: []);

        Assert.Contains("None yet", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_layer_name_in_the_tree_is_encoded()
    {
        // The tree is another place a user-supplied name reaches markup, and it
        // is a separate code path from the service list and the property table.
        string page = RestDirectory.Document(
            "/rest/services/x/FeatureServer",
            "x",
            new { id = 0 },
            tree: [("<script>alert(1)</script>", "/x/0", 0)]);

        Assert.DoesNotContain("<script>", page, System.StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", page, System.StringComparison.Ordinal);
    }

    private static int Count(string text, string needle)
    {
        int total = 0;
        int at = 0;

        while ((at = text.IndexOf(needle, at, System.StringComparison.Ordinal)) >= 0)
        {
            total++;
            at += needle.Length;
        }

        return total;
    }
}
