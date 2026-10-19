using System.Collections.Generic;
using System.Linq;
using Graticula.Host;
using Microsoft.Extensions.DependencyInjection;
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
    public void TheVersionIsWrittenAsEsriWritesIt()
    {
        // ADR-181: two decimals were fixed, so 12.1 read 12.10.
        string page = RestDirectory.Folder(
            "/rest/services", folder: null, version: 12.1, folders: [], services: []);

        Assert.Contains("version 12.1<", page, System.StringComparison.Ordinal);
        Assert.Contains("version 10.81<", RestDirectory.Folder(
            "/rest/services", folder: null, version: 10.81, folders: [], services: []), System.StringComparison.Ordinal);
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

    // ---------- numbers, colours and what the chrome offers (UX sweep 2026-10-19) ----------

    private static readonly int[] Blue = [68, 119, 170, 255];
    private static readonly int[] Box = [1, 2, 3, 4];
    private static readonly string[] Project = ["project"];

    [Fact]
    public void An_identifier_prints_as_the_digits_a_client_accepts()
    {
        // `102,100` is not a reference any client takes, and it is what a developer copied.
        string page = RestDirectory.Document(
            "/rest/services/x/FeatureServer/0",
            "x",
            new
            {
                extent = new { spatialReference = new { wkid = 102100, latestWkid = 3857 } },
                fields = new[] { new { name = "note", length = 1073741822 } },
            });

        Assert.Contains(">102100<", page, System.StringComparison.Ordinal);
        Assert.Contains(">3857<", page, System.StringComparison.Ordinal);
        Assert.Contains(">1073741822<", page, System.StringComparison.Ordinal);
        Assert.DoesNotContain("102,100", page, System.StringComparison.Ordinal);
        Assert.DoesNotContain("3,857", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public void An_amount_meant_for_reading_keeps_its_separators()
    {
        string page = RestDirectory.Document(
            "/rest/services/x/FeatureServer/0", "x", new { maxRecordCount = 50000 });

        Assert.Contains(">50,000<", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_colour_is_a_swatch_and_its_text_rather_than_four_bullets()
    {
        string page = RestDirectory.Document(
            "/rest/services/x/FeatureServer/0",
            "x",
            new { drawingInfo = new { renderer = new { symbol = new { color = Blue } } } });

        Assert.Contains("class=\"swatch\"", page, System.StringComparison.Ordinal);
        Assert.Contains("<code>rgba(68,119,170,255)</code>", page, System.StringComparison.Ordinal);
        Assert.DoesNotContain("<li><span class=\"v\">68</span></li>", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Four_numbers_under_another_key_are_not_a_colour()
    {
        // A bounding box is four numbers too; a swatch for one would be a lie with a colour.
        string page = RestDirectory.Document(
            "/rest/services/x/FeatureServer/0", "x", new { bbox = Box });

        Assert.DoesNotContain("class=\"swatch\"", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public void The_header_offers_Studio_as_well_as_Server()
    {
        string page = RestDirectory.Folder(
            "/rest/services", folder: null, version: 12.1, folders: ["hosted"], services: []);

        Assert.Contains("<a href=\"/studio/\">Studio</a>", page, System.StringComparison.Ordinal);
        Assert.Contains("<a href=\"/server/\">Server</a>", page, System.StringComparison.Ordinal);
        Assert.Contains("rel=\"icon\" href=\"/favicon.svg\"", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Many_formats_go_behind_one_disclosure_and_a_few_stay_on_the_line()
    {
        (string, string)[] ten = [.. Enumerable.Range(1, 9).Select(i => ($"F{i}", $"/f{i}"))];

        string root = RestDirectory.Folder(
            "/rest/services", null, 12.1, ["hosted"], [], formats: ten);

        Assert.Contains("<summary>Other formats (9)</summary>", root, System.StringComparison.Ordinal);
        Assert.Contains("href=\"/rest/services?f=json\">JSON</a><details", root, System.StringComparison.Ordinal);

        string service = RestDirectory.Document(
            "/rest/services/x/FeatureServer", "x", new { id = 0 }, formats: [("WFS", "/wfs")]);

        Assert.DoesNotContain("<details class=\"more\">", service, System.StringComparison.Ordinal);
        Assert.Contains(">WFS</a>", service, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_card_says_how_many_services_it_holds()
    {
        string page = RestDirectory.Folder(
            "/rest/services",
            null,
            12.1,
            ["hosted", "empty", "Utilities"],
            [],
            folderCounts: new Dictionary<string, int> { ["hosted"] = 18, ["Utilities"] = 1 });

        Assert.Contains("<span class=\"n\">18 services</span>", page, System.StringComparison.Ordinal);
        Assert.Contains("<span class=\"n\">1 service</span>", page, System.StringComparison.Ordinal);
        Assert.Contains("<span class=\"n\">0 services</span>", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Without_counts_a_folder_card_claims_none()
    {
        // Blind, the caller cannot count; a card saying 0 would be a wrong number, not an absent one.
        string page = RestDirectory.Folder(
            "/rest/services", null, 12.1, ["hosted"], []);

        Assert.DoesNotContain("class=\"n\"", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_service_tree_is_not_followed_by_the_same_layers_again()
    {
        string page = RestDirectory.Document(
            "/rest/services/x/FeatureServer",
            "x",
            new { layers = new[] { new { id = 0, name = "roads" } }, maxRecordCount = 1000 },
            tree: [("roads (0)", "/x/0", 0)]);

        Assert.Contains("roads (0)", page, System.StringComparison.Ordinal);
        Assert.DoesNotContain(">Layers</th>", page, System.StringComparison.Ordinal);
        Assert.Contains("Max Record Count", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public void An_omitted_property_is_left_out_of_the_table()
    {
        string page = RestDirectory.Document(
            "/rest/services/Utilities/Geometry/GeometryServer",
            "Geometry",
            new { supportedOperations = Project, maximumVertices = 10 },
            links: [("project", "/p")],
            linksLabel: "Supported operations",
            omit: ["supportedOperations"]);

        Assert.DoesNotContain("<li>project</li>", page, System.StringComparison.Ordinal);
        Assert.Contains("href=\"/p\">project</a>", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_refusal_page_carries_the_sentence_encoded()
    {
        string page = RestDirectory.Refusal(
            "/rest/services/hosted/nosuch/FeatureServer",
            "Not found",
            "No service '<script>alert(1)</script>' is visible to you.");

        Assert.DoesNotContain("<script>", page, System.StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)", page, System.StringComparison.Ordinal);
        Assert.Contains("<h1>Not found</h1>", page, System.StringComparison.Ordinal);
        Assert.Contains("href=\"/rest/services/hosted\"", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public void The_directory_does_not_carry_the_old_teal()
    {
        string page = RestDirectory.Folder("/rest/services", null, 12.1, [], []);

        Assert.DoesNotContain("#0d7d70", page, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#10151b", page, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--accent-ink: #1257c2", page, System.StringComparison.Ordinal);
        Assert.Contains("--paper: #0a1120", page, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("?f=html", null, "text/html")]
    [InlineData("", "text/html,application/xhtml+xml", "text/html")]
    [InlineData("?f=json", "text/html", "application/json")]
    [InlineData("?f=pjson", null, "application/json")]
    [InlineData("", null, "application/json")]
    public async System.Threading.Tasks.Task A_refusal_is_a_page_only_for_a_browser_in_the_directory(
        string query, string? accept, string media)
    {
        // Round 2: a missing layer of a real service still answered a browser with raw JSON.
        Microsoft.AspNetCore.Http.DefaultHttpContext context = new();
        context.RequestServices = new ServiceCollection()
            .AddLogging().BuildServiceProvider();
        context.Request.Path = "/rest/services/hosted/roads/FeatureServer/9";
        context.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString(query);

        if (accept is not null)
        {
            context.Request.Headers.Accept = accept;
        }

        System.IO.MemoryStream body = new();
        context.Response.Body = body;

        await RestDirectory.RefuseAsync(context, 404, "Not found", "The service 'hosted/roads' has no layer 9.");

        Assert.Equal(404, context.Response.StatusCode);
        Assert.StartsWith(media, context.Response.ContentType, System.StringComparison.Ordinal);
        Assert.Contains(
            "has no layer 9.",
            System.Text.Encoding.UTF8.GetString(body.ToArray()),
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_that_scrolls_can_take_focus_and_has_a_name()
    {
        // axe scrollable-region-focusable: a keyboard user could not scroll past the first screen.
        string page = RestDirectory.Document(
            "/rest/services/x/FeatureServer/0", "x", new { fields = new[] { new { name = "il" } } });

        Assert.Contains(
            "<div class=\"scroll\" tabindex=\"0\" role=\"region\" aria-label=\"Fields\">",
            page,
            System.StringComparison.Ordinal);
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

        // <b>`"<li"` rather than `"<li>"`, because an opening tag may carry attributes.</b>
        // This counted the literal `<li>` and so missed the breadcrumb's current item, which
        // is `<li aria-current="page">` — it saw the close and not the open and reported an
        // imbalance that was not there. Counting the tag name catches every spelling, and
        // `</li>` cannot match it because of the slash. <b>And `"<li>"` plus `"<li "`, not `"<li"`,
        // since the head gained `<link rel="icon">` on 2026-10-19</b>: a tag *name* is followed by
        // `>` or a space, and `<link` is a different element that has no closing tag.
        Assert.Equal(Count(page, "<ul"), Count(page, "</ul>"));
        Assert.Equal(Count(page, "<li>") + Count(page, "<li "), Count(page, "</li>"));
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
