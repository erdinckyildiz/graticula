using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The form client's screens, ADR-287 §5.10 and §5.15, 2026-11-15: guidance hints and pictures, a group as one page, a
/// table of selects, link parameters, the thank-you screen and the ground under the map. The server is the stand-in of
/// <see cref="FormFillClientTests.Site"/>; the drafts are tested in <see cref="FormFillClientTests"/>.
/// </summary>
public sealed class FormFillClientScreensTests : IClassFixture<FormFillClientTests.Site>
{
    private static readonly byte[] Svg = Encoding.UTF8.GetBytes(
        """<svg xmlns="http://www.w3.org/2000/svg" width="40" height="20"><rect width="40" height="20" fill="#1565d8"/></svg>""");

    private readonly FormFillClientTests.Site _site;

    /// <summary>Creates the tests over the site.</summary>
    /// <param name="site">The stand-in server and the browser, opened once for the class.</param>
    public FormFillClientScreensTests(FormFillClientTests.Site site) => _site = site;

    private static JsonObject Choices(string list, params (string Name, string Label, string? Image)[] rows) => new()
    {
        [list] = new JsonArray([.. rows.Select(r => (JsonNode)FormFillClientTests.Form.Choice(r.Name, r.Label, r.Image))]),
    };

    // ------------------------------------------------------------------------------------------- guidance hints

    [Fact]
    public async Task A_guidance_hint_waits_behind_a_button_that_says_whether_it_is_open()
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [FormFillClientTests.Form.Question("ad", "text", "Ad", hint: "Kısa", guidance: "Uzun açıklama burada.")]),
        });
        await _site.OpenAsync(id);

        Assert.Equal("false", await _site.EvalAsync<string>("document.querySelector('[data-name=ad] .guidance-toggle').getAttribute('aria-expanded')"));
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=ad] .guidance').hidden"), "The guidance showed before it was asked for.");
        Assert.Equal("More help", await _site.EvalAsync<string>("document.querySelector('[data-name=ad] .guidance-toggle').textContent"));

        await _site.EvalAsync<bool>("document.querySelector('[data-name=ad] .guidance-toggle').click(); true");
        Assert.Equal("true", await _site.EvalAsync<string>("document.querySelector('[data-name=ad] .guidance-toggle').getAttribute('aria-expanded')"));
        Assert.False(await _site.EvalAsync<bool>("document.querySelector('[data-name=ad] .guidance').hidden"));
        Assert.Contains("Uzun açıklama", await _site.EvalAsync<string>("document.querySelector('[data-name=ad] .guidance').textContent"));

        // The button controls the panel it opens, and both are reachable: a button, in the tab order, and named.
        string controls = await _site.EvalAsync<string>("document.querySelector('[data-name=ad] .guidance-toggle').getAttribute('aria-controls')") ?? "";
        Assert.True(await _site.EvalAsync<bool>($"!!document.getElementById('{controls}')"));
        Assert.Equal("BUTTON", await _site.EvalAsync<string>("document.querySelector('[data-name=ad] .guidance-toggle').tagName"));
    }

    [Fact]
    public async Task An_open_guidance_hint_stays_open_and_focused_when_the_page_is_drawn_again()
    {
        FormFillClientTests.Form form = new()
        {
            Pages = true,
            Root = FormFillClientTests.Form.Repeat("", [
                FormFillClientTests.Form.Group("bir", "Bir", [FormFillClientTests.Form.Question("ad", "text", "Ad", guidance: "Yardım.")]),
                FormFillClientTests.Form.Group("iki", "İki", [FormFillClientTests.Form.Question("yas", "integer", "Yaş")]),
            ]),
        };
        string id = _site.Publish(form);
        await _site.OpenAsync(id);

        await _site.EvalAsync<bool>("document.querySelector('[data-name=ad] .guidance-toggle').click(); true");
        await _site.EvalAsync<bool>("document.getElementById('next').click(); true");
        await _site.WaitAsync("!!document.querySelector('[data-name=yas]')", "the second page");
        await _site.EvalAsync<bool>("document.getElementById('back').click(); true");
        await _site.WaitAsync("!!document.querySelector('[data-name=ad]')", "the first page again");
        Assert.Equal("true", await _site.EvalAsync<string>("document.querySelector('[data-name=ad] .guidance-toggle').getAttribute('aria-expanded')"));
        Assert.False(await _site.EvalAsync<bool>("document.querySelector('[data-name=ad] .guidance').hidden"));
    }

    // ----------------------------------------------------------------------------------------------- pictures

    [Fact]
    public async Task A_picture_on_a_question_and_its_choices_is_shown_from_the_forms_files()
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [
                FormFillClientTests.Form.Question("logo", "note", "Logo", image: "logo.svg"),
                FormFillClientTests.Form.Question("renk", "select_one", "Renk", list: "renk"),
                FormFillClientTests.Form.Question("kayip", "note", "Kayıp", image: "yok.png"),
            ]),
            Choices = Choices("renk", ("kirmizi", "Kırmızı", "kirmizi.svg"), ("mavi", "Mavi", "mavi.svg")),
            Media = new() { ["media/logo.svg"] = Svg, ["kirmizi.svg"] = Svg, ["media/mavi.svg"] = Svg },
        });
        await _site.OpenAsync(id);

        await _site.WaitAsync("document.querySelectorAll('[data-name=logo] .question-picture img').length === 1", "the question's picture");
        await _site.WaitAsync("document.querySelectorAll('[data-name=renk] .choice-picture img').length === 2", "both choices' pictures");
        await _site.WaitAsync("Array.prototype.every.call(document.querySelectorAll('.picture img'), function (i) { return i.complete && i.naturalWidth > 0; })", "the pictures to decode");
        Assert.StartsWith("blob:", await _site.EvalAsync<string>("document.querySelector('[data-name=logo] .question-picture img').src") ?? "");
        // The picture is decoration beside text that says the same: no alt text to read twice.
        Assert.Equal(string.Empty, await _site.EvalAsync<string>("document.querySelector('[data-name=logo] .question-picture img').getAttribute('alt')"));

        // A picture that is not among the files is said, in words, where it was to be.
        await _site.WaitAsync("document.querySelector('[data-name=kayip]').textContent.indexOf('yok.png') >= 0", "the missing picture to be named");
        Assert.Equal(0, await _site.EvalAsync<int>("document.querySelectorAll('[data-name=kayip] img').length"));
    }

    [Fact]
    public async Task A_picture_is_kept_on_the_device_with_the_form_and_shows_with_no_server()
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [FormFillClientTests.Form.Question("logo", "note", "Logo", image: "logo.svg")]),
            Media = new() { ["media/logo.svg"] = Svg },
        });
        await _site.OpenAsync(id);
        await _site.WaitAsync("!!navigator.serviceWorker.controller", "the service worker to take the page", seconds: 20);
        await _site.OpenAsync(id);
        await _site.WaitAsync("document.querySelectorAll('.picture img').length === 1", "the picture");
        await _site.EvalAsync<bool>("document.getElementById('download-offline').click(); true");
        await _site.WaitAsync($"{FormFillClientTests.Site.IdbAll("forms")}.then(function (f) {{ var x = f.filter(function (r) {{ return r.id === '{id}'; }})[0]; return !!x && !!x.media && x.media['logo.svg'] instanceof Blob; }})",
            "the picture to be stored with the form", seconds: 15);

        _site.Stop();
        try
        {
            await _site.OpenAsync(id, expectServer: false);
            await _site.WaitAsync("document.querySelectorAll('.picture img').length === 1", "the picture, from the device", seconds: 20);
        }
        finally
        {
            _site.Start();
        }
    }

    // ------------------------------------------------------------------------------ field-list and table-list

    [Fact]
    public async Task A_field_list_group_inside_a_group_is_a_page_of_its_own_under_the_pages_style()
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Pages = true,
            Root = FormFillClientTests.Form.Repeat("", [
                FormFillClientTests.Form.Group("ust", "Üst", [
                    FormFillClientTests.Form.Question("a", "text", "A"),
                    FormFillClientTests.Form.Group("alt", "Alt", [FormFillClientTests.Form.Question("b", "text", "B"), FormFillClientTests.Form.Question("c", "text", "C")], "field-list"),
                    FormFillClientTests.Form.Question("d", "text", "D"),
                ]),
            ]),
        });
        await _site.OpenAsync(id);

        async Task<string> OnPageAsync() => string.Join(",", await _site.EvalAsync<string[]>(
            "Array.prototype.map.call(document.querySelectorAll('#body [data-name]'), function (n) { return n.getAttribute('data-name'); })") ?? []);

        Assert.Equal("Page 1 of 3", await _site.EvalAsync<string>("document.getElementById('pages-progress').textContent"));
        Assert.Equal("a", await OnPageAsync());
        await _site.EvalAsync<bool>("document.getElementById('next').click(); true");
        await _site.WaitAsync("!!document.querySelector('[data-name=b]')", "the field-list page");
        Assert.Equal("b,c", await OnPageAsync());
        // The parent's heading is above it, for context.
        Assert.Contains("Üst", await _site.EvalAsync<string>("document.getElementById('body').textContent"));
        await _site.EvalAsync<bool>("document.getElementById('next').click(); true");
        await _site.WaitAsync("!!document.querySelector('[data-name=d]')", "the last page");
        Assert.Equal("d", await OnPageAsync());
        Assert.False(await _site.EvalAsync<bool>("document.getElementById('submit').hidden"));
    }

    [Fact]
    public async Task A_table_list_group_is_a_table_whose_cells_are_named_by_row_and_column_and_whose_answers_are_sent()
    {
        FormFillClientTests.Form form = new()
        {
            Root = FormFillClientTests.Form.Repeat("", [
                FormFillClientTests.Form.Group("matris", "Memnuniyet", [
                    FormFillClientTests.Form.Question("hiz", "select_one", "Hız", required: "yes", list: "olcek"),
                    FormFillClientTests.Form.Question("temizlik", "select_one", "Temizlik", list: "olcek"),
                    FormFillClientTests.Form.Question("neler", "select_multiple", "Neler", list: "olcek"),
                ], "table-list"),
            ]),
            Choices = Choices("olcek", ("iyi", "İyi", null), ("orta", "Orta", null), ("kotu", "Kötü", null)),
        };
        string id = _site.Publish(form);
        await _site.OpenAsync(id);

        Assert.Equal(3, await _site.EvalAsync<int>("document.querySelectorAll('table.matrix tbody tr').length"));
        Assert.Equal(4, await _site.EvalAsync<int>("document.querySelectorAll('table.matrix thead th').length"));
        Assert.Equal(9, await _site.EvalAsync<int>("document.querySelectorAll('table.matrix tbody input').length"));
        Assert.Equal("region", await _site.EvalAsync<string>("document.querySelector('.matrix-scroll').getAttribute('role')"));
        // Every cell is named by its row header and its column header, both of which exist.
        Assert.True(await _site.EvalAsync<bool>("""
            Array.prototype.every.call(document.querySelectorAll('table.matrix tbody input'), function (i) {
              return i.getAttribute('aria-labelledby').split(' ').every(function (id) { return !!document.getElementById(id) && document.getElementById(id).textContent.trim().length > 0; });
            })
            """), "A cell has no name.");

        // Submitting with the required row empty says so on that row and names it in the summary.
        await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");
        await _site.WaitAsync("!document.getElementById('summary').hidden", "the summary");
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=hiz] input').getAttribute('aria-invalid') === 'true'"));
        Assert.True(await _site.EvalAsync<bool>("!!document.querySelector('[data-name=hiz] .error').textContent"));
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=hiz]').contains(document.activeElement)"), "Focus did not go to the row with the problem.");

        // Choosing a cell answers the row; a row of checkboxes takes several.
        await _site.EvalAsync<bool>("""
            (function () {
              document.querySelector('[data-name=hiz] input[value=orta]').click();
              document.querySelector('[data-name=temizlik] input[value=kotu]').click();
              document.querySelector('[data-name=neler] input[value=iyi]').click();
              document.querySelector('[data-name=neler] input[value=kotu]').click();
              return true;
            })()
            """);
        Assert.Equal(string.Empty, await _site.EvalAsync<string>("document.querySelector('[data-name=hiz] .error').textContent"));
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=hiz] input[value=orta]').checked && !document.querySelector('[data-name=hiz] input[value=iyi]').checked"));

        await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");
        await _site.WaitAsync("!document.getElementById('done').hidden", "the confirmation");
        string adds = _site.Adds[^1];
        JsonObject row = JsonNode.Parse(adds)!.AsArray()[0]!["attributes"]!.AsObject();
        Assert.Equal("orta", (string?)row["hiz"]);
        Assert.Equal("kotu", (string?)row["temizlik"]);
        Assert.Equal("iyi,kotu", (string?)row["neler"]);
    }

    // ------------------------------------------------------------------------------------------ link parameters

    [Fact]
    public async Task A_link_fills_a_new_form_in_and_says_what_it_could_not_use()
    {
        FormFillClientTests.Form form = new()
        {
            Root = FormFillClientTests.Form.Repeat("", [
                FormFillClientTests.Form.Question("ad", "text", "Ad"),
                FormFillClientTests.Form.Question("yas", "integer", "Yaş"),
                FormFillClientTests.Form.Question("renk", "select_one", "Renk", list: "renk"),
                FormFillClientTests.Form.Question("hepsi", "select_multiple", "Hepsi", list: "renk"),
                FormFillClientTests.Form.Question("gun", "date", "Gün"),
                FormFillClientTests.Form.Question("konum", "geopoint", "Konum"),
                FormFillClientTests.Form.Question("foto", "image", "Foto"),
            ]),
            Choices = Choices("renk", ("kirmizi", "Kırmızı", null), ("mavi", "Mavi", null)),
        };
        string id = _site.Publish(form);
        await _site.OpenAsync(id, "?field:ad=Ay%C5%9Fe&field:yas=abc&field:renk=mavi&field:hepsi=mavi,kirmizi&field:gun=2026-02-30&field:konum=41.5,29.25"
            + "&field:foto=x&field:yok=1&hide=header,bogus&center=95,29");

        Assert.Equal("Ayşe", await _site.EvalAsync<string>("document.querySelector('[data-name=ad] input').value"));
        Assert.Equal(string.Empty, await _site.EvalAsync<string>("document.querySelector('[data-name=yas] input').value"));
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=renk] input[value=mavi]').checked"));
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=hepsi] input[value=mavi]').checked && document.querySelector('[data-name=hepsi] input[value=kirmizi]').checked"));
        Assert.Equal("41.5", await _site.EvalAsync<string>("document.querySelector('[data-name=konum] input[data-focus=lat]').value"));

        // Everything that was not used is listed, by parameter, in a region that is announced.
        Assert.False(await _site.EvalAsync<bool>("document.getElementById('link-notices').hidden"));
        Assert.Equal("status", await _site.EvalAsync<string>("document.getElementById('link-notices').getAttribute('role')"));
        string notices = await _site.EvalAsync<string>("document.getElementById('link-notices-list').textContent") ?? "";
        foreach (string expected in new[] { "field:yas=abc", "field:gun=2026-02-30", "field:foto=x", "field:yok=1", "hide=bogus", "center=95,29" })
        {
            Assert.Contains(expected, notices);
        }

        Assert.DoesNotContain("field:ad", notices);
        Assert.True(await _site.EvalAsync<bool>("document.documentElement.classList.contains('hide-header')"));

        // A prefilled form is not yet a draft: nobody entered anything.
        await Task.Delay(1200);
        Assert.Equal(0, await _site.EvalAsync<int>($"{FormFillClientTests.Site.Drafts(id)}.then(function (d) {{ return d.length; }})"));

        // The notice can be dismissed, and focus goes somewhere that exists.
        await _site.EvalAsync<bool>("document.getElementById('link-notices-dismiss').click(); true");
        Assert.True(await _site.EvalAsync<bool>("document.getElementById('link-notices').hidden"));
        Assert.Equal("form-main", await _site.EvalAsync<string>("document.activeElement.id"));
    }

    [Fact]
    public async Task A_link_with_answers_in_it_does_not_reopen_the_draft_beside_it_and_a_plain_link_does()
    {
        string id = _site.Publish(FormFillClientTests.Form.Basic());
        await _site.OpenAsync(id);
        await _site.SetAsync("ad", "Eski");
        await _site.WaitAsync($"{FormFillClientTests.Site.Drafts(id)}.then(function (d) {{ return d.length === 1; }})", "the draft");

        await _site.OpenAsync(id, "?field:ad=Yeni");
        Assert.Equal("Yeni", await _site.EvalAsync<string>("document.querySelector('[data-name=ad] input').value"));
        // The earlier draft is still there to resume.
        Assert.Equal("1", await _site.EvalAsync<string>("document.getElementById('drafts-count').textContent"));

        await _site.OpenAsync(id);
        await _site.WaitAsync("document.getElementById('draft-reopened').textContent.indexOf('reopened') > 0", "the draft to reopen");
        Assert.Equal("Eski", await _site.EvalAsync<string>("document.querySelector('[data-name=ad] input').value"));
    }

    [Fact]
    public async Task Hide_takes_parts_of_the_page_away_and_center_opens_the_location_map_there()
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [FormFillClientTests.Form.Question("konum", "geopoint", "Konum")]),
        });
        await _site.OpenAsync(id, "?hide=navbar,offline&center=41.01,28.97");

        Assert.Equal("none", await _site.EvalAsync<string>("getComputedStyle(document.querySelector('.bar-tools')).display"));
        Assert.Equal("none", await _site.EvalAsync<string>("getComputedStyle(document.getElementById('offline-panel')).display"));
        await _site.WaitAsync("!!document.querySelector('.qmap .ol-viewport') && GraticulaFormFill.maps().length === 1", "the map");
        double[] view = await _site.EvalAsync<double[]>("GraticulaFormFill.maps()[0].view") ?? [];
        Assert.Equal(28.97, view[0], 3);
        Assert.Equal(41.01, view[1], 3);
        Assert.True(view[2] >= 15, $"The map opened at zoom {view[2]}, too far out for a tap to place a point.");

        // Without it the map opens over the country, as before.
        await _site.OpenAsync(id);
        await _site.WaitAsync("!!document.querySelector('.qmap .ol-viewport') && GraticulaFormFill.maps().length === 1", "the map");
        Assert.Equal(5, (await _site.EvalAsync<double[]>("GraticulaFormFill.maps()[0].view") ?? [])[2], 3);
    }

    // ---------------------------------------------------------------------------------- thank-you and redirect

    [Fact]
    public async Task The_publishers_thank_you_replaces_the_pages_sentence_after_an_answer_was_sent()
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [FormFillClientTests.Form.Question("ad", "text", "Ad")]),
            Settings = """{"thankYou":"Teşekkürler, kaydınız alındı."}""",
        });
        await _site.OpenAsync(id);
        await _site.SetAsync("ad", "Ali");
        await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");
        await _site.WaitAsync("!document.getElementById('done').hidden", "the thank-you");

        Assert.Equal("Teşekkürler, kaydınız alındı.", await _site.EvalAsync<string>("document.getElementById('done-text').textContent"));
        Assert.True(await _site.EvalAsync<bool>("document.getElementById('redirect').hidden"), "A form with no redirect offered one.");
    }

    [Fact]
    public async Task A_redirect_says_where_it_goes_and_can_be_declined_or_taken_at_once()
    {
        string other = _site.Publish(FormFillClientTests.Form.Basic());
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [FormFillClientTests.Form.Question("ad", "text", "Ad")]),
            Settings = $$"""{"redirect":"{{_site.Address}}/forms/{{other}}"}""",
        });
        await _site.OpenAsync(id);
        await _site.SetAsync("ad", "Ali");
        await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");
        await _site.WaitAsync("!document.getElementById('redirect').hidden", "the redirect offer");

        Assert.Contains("127.0.0.1", await _site.EvalAsync<string>("document.getElementById('redirect-text').textContent"));
        Assert.Equal("status", await _site.EvalAsync<string>("document.getElementById('redirect-text').getAttribute('role')"));
        Assert.Equal($"{_site.Address}/forms/{other}", await _site.EvalAsync<string>("document.getElementById('redirect-go').href"));

        await _site.EvalAsync<bool>("document.getElementById('redirect-stay').click(); true");
        Assert.True(await _site.EvalAsync<bool>("document.getElementById('redirect').hidden"));
        Assert.Equal("new-form", await _site.EvalAsync<string>("document.activeElement.id"));
        await Task.Delay(6500);
        Assert.EndsWith($"/forms/{id}", await _site.EvalAsync<string>("location.pathname") ?? "");
    }

    [Fact]
    public async Task A_redirect_that_nobody_stops_takes_the_person_on_after_a_few_seconds()
    {
        string other = _site.Publish(FormFillClientTests.Form.Basic());
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [FormFillClientTests.Form.Question("ad", "text", "Ad")]),
            Settings = $$"""{"redirect":"{{_site.Address}}/forms/{{other}}"}""",
        });
        await _site.OpenAsync(id);
        await _site.SetAsync("ad", "Ali");
        await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");
        await _site.WaitAsync($"location.pathname === '/forms/{other}'", "the redirect", seconds: 20);
    }

    [Fact]
    public async Task A_redirect_that_is_not_a_web_address_is_never_followed_even_if_the_form_carries_one()
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [FormFillClientTests.Form.Question("ad", "text", "Ad")]),
            Settings = """{"redirect":"javascript:alert(1)","thankYou":"Tamam."}""",
        });
        await _site.OpenAsync(id);
        await _site.SetAsync("ad", "Ali");
        await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");
        await _site.WaitAsync("!document.getElementById('done').hidden", "the thank-you");
        Assert.True(await _site.EvalAsync<bool>("document.getElementById('redirect').hidden"));
        Assert.Equal("Tamam.", await _site.EvalAsync<string>("document.getElementById('done-text').textContent"));
    }

    // -------------------------------------------------------------------------------------------- the map's ground

    [Theory]
    [InlineData(null, "osm")]
    [InlineData("osm", "osm")]
    [InlineData("topo", "topo")]
    [InlineData("none", "none")]
    [InlineData("satellite", "osm")]
    public async Task The_ground_under_the_map_is_the_publishers_choice_and_osm_when_there_is_none(string? chosen, string expected)
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [FormFillClientTests.Form.Question("konum", "geopoint", "Konum")]),
            Settings = chosen is null ? "{}" : $$"""{"basemap":"{{chosen}}"}""",
        });
        await _site.OpenAsync(id);
        await _site.WaitAsync("!!document.querySelector('.qmap .ol-viewport') && GraticulaFormFill.maps().length === 1", "the map");
        Assert.Equal(expected, await _site.EvalAsync<string>("GraticulaFormFill.maps()[0].ground"));
        Assert.Equal(expected, await _site.EvalAsync<string>("document.querySelector('.qmap').getAttribute('data-ground')"));
    }
}
