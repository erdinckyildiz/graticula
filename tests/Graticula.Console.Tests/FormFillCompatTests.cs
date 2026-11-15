using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The form client's Survey123 widgets, ADR-287 §5.19, 2026-11-15: a group or repeat that is compact or minimal, an
/// autocomplete, an input mask, a spinner, a week number, the Other that <c>or_other</c> adds and a list with a choice
/// named twice. The server is the stand-in of <see cref="FormFillClientTests.Site"/>, so these test the page and nothing
/// else; what the real server sends for each is tested in <c>AFormImportsSurvey123SpellingsTests</c>.
/// </summary>
public sealed class FormFillCompatTests : IClassFixture<FormFillClientTests.Site>
{
    private readonly FormFillClientTests.Site _site;

    /// <summary>Creates the tests over the site.</summary>
    /// <param name="site">The stand-in server and the browser, opened once for the class.</param>
    public FormFillCompatTests(FormFillClientTests.Site site) => _site = site;

    private static JsonObject Q(string name, string type, string label, string? required = null, string? list = null, params string[] appearance) =>
        FormFillClientTests.Form.Question(name, type, label, required, list, null, null, null, appearance);

    private static JsonObject With(JsonObject node, string key, JsonNode? value)
    {
        node[key] = value;
        return node;
    }

    private static JsonObject Choices(string list, params JsonObject[] rows) => new() { [list] = new JsonArray([.. rows.Select(r => (JsonNode)r)]) };

    private static JsonObject Choice(string name, string label, string? filterColumn = null, string? filterValue = null, bool auto = false)
    {
        JsonObject c = FormFillClientTests.Form.Choice(name, label);

        if (filterColumn is not null)
        {
            c["filters"] = new JsonObject { [filterColumn] = filterValue };
        }

        if (auto)
        {
            c["auto"] = true;
        }

        return c;
    }

    private Task<bool> KeyAsync(string selector, string key) => _site.EvalAsync<bool>($$"""
        (function () {
          var n = document.querySelector('{{selector}}');
          n.focus();
          n.dispatchEvent(new KeyboardEvent('keydown', { key: '{{key}}', bubbles: true, cancelable: true }));
          return true;
        })()
        """);

    // A tab with no system focus does not fire blur from .blur(), so the event is sent as well; the handler is idempotent.
    private Task<bool> BlurAsync(string selector) => _site.EvalAsync<bool>($$"""
        (function () {
          var n = document.querySelector('{{selector}}');
          n.blur();
          n.dispatchEvent(new FocusEvent('blur'));
          return true;
        })()
        """);

    private Task<bool> TypeAsync(string selector, string value) => _site.EvalAsync<bool>($$"""
        (function () {
          var n = document.querySelector('{{selector}}');
          n.focus();
          n.value = {{System.Text.Json.JsonSerializer.Serialize(value)}};
          n.dispatchEvent(new Event('input', { bubbles: true }));
          return true;
        })()
        """);

    // ----------------------------------------------------------------------------------------- compact and minimal

    [Fact]
    public async Task A_compact_group_starts_collapsed_behind_a_button_that_says_whether_it_is_open_and_a_minimal_one_does_too()
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [
                FormFillClientTests.Form.Group("a", "Birinci", [Q("ad", "text", "Ad")], "compact"),
                FormFillClientTests.Form.Group("b", "İkinci", [Q("yas", "integer", "Yaş")], "minimal"),
                FormFillClientTests.Form.Group("c", "Üçüncü", [Q("not", "text", "Not")]),
            ]),
        });
        await _site.OpenAsync(id);

        // Collapsed at startup: the heading is a button, the panel it controls is hidden, and the plain group is not folded.
        Assert.Equal(2, await _site.EvalAsync<int>("document.querySelectorAll('.collapse-toggle').length"));
        Assert.Equal("false", await _site.EvalAsync<string>("document.querySelector('[data-key=a] .collapse-toggle').getAttribute('aria-expanded')"));
        Assert.Equal("BUTTON", await _site.EvalAsync<string>("document.querySelector('[data-key=a] .collapse-toggle').tagName"));
        Assert.Equal("H2", await _site.EvalAsync<string>("document.querySelector('[data-key=a] .collapse-toggle').parentElement.tagName"));
        string panel = await _site.EvalAsync<string>("document.querySelector('[data-key=a] .collapse-toggle').getAttribute('aria-controls')") ?? "";
        Assert.True(await _site.EvalAsync<bool>($"document.getElementById('{panel}').hidden"), "The group was open at startup.");
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-key=a] .collapse-panel').contains(document.querySelector('[data-name=ad]'))"));
        Assert.False(await _site.EvalAsync<bool>("document.querySelector('[data-key=c]').querySelector('[data-name=not]').offsetParent === null"), "The plain group was folded.");
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=ad]').offsetParent === null"), "A folded question is reachable.");

        // The button opens it, and says so; a second press closes it.
        await _site.EvalAsync<bool>("document.querySelector('[data-key=a] .collapse-toggle').click(); true");
        Assert.Equal("true", await _site.EvalAsync<string>("document.querySelector('[data-key=a] .collapse-toggle').getAttribute('aria-expanded')"));
        Assert.False(await _site.EvalAsync<bool>($"document.getElementById('{panel}').hidden"));
        Assert.False(await _site.EvalAsync<bool>("document.querySelector('[data-name=ad]').offsetParent === null"));
        await _site.EvalAsync<bool>("document.querySelector('[data-key=a] .collapse-toggle').click(); true");
        Assert.True(await _site.EvalAsync<bool>($"document.getElementById('{panel}').hidden"));

        // It is a 44 px target on a phone as it is here.
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-key=a] .collapse-toggle').getBoundingClientRect().height >= 44"));
    }

    [Fact]
    public async Task What_the_person_opened_stays_open_and_keeps_focus_when_the_page_is_drawn_again()
    {
        JsonObject room = FormFillClientTests.Form.Repeat("oda", [Q("oda_ad", "text", "Oda adı")]);
        room["repeatCount"] = "${n}";
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [
                Q("n", "integer", "Kaç oda"),
                FormFillClientTests.Form.Group("a", "Birinci", [Q("ad", "text", "Ad")], "compact"),
                room,
            ]),
        });
        await _site.OpenAsync(id);

        await _site.EvalAsync<bool>("document.querySelector('[data-key=a] .collapse-toggle').click(); true");

        // Focus is on the button when the page is drawn again, which a repeat's count changing does.
        await _site.EvalAsync<bool>("""
            (function () {
              document.querySelector('[data-key=a] .collapse-toggle').focus();
              var n = document.querySelector('[data-name=n] input');
              n.value = '2';
              n.dispatchEvent(new Event('input', { bubbles: true }));
              return true;
            })()
            """);
        await _site.WaitAsync("document.querySelectorAll('[data-key^=oda]').length >= 2", "the page to be drawn with two rooms");
        Assert.Equal("true", await _site.EvalAsync<string>("document.querySelector('[data-key=a] .collapse-toggle').getAttribute('aria-expanded')"));
        Assert.True(await _site.EvalAsync<bool>("document.activeElement === document.querySelector('[data-key=a] .collapse-toggle')"), "Focus was dropped by the redraw.");
    }

    [Fact]
    public async Task A_message_inside_a_collapsed_group_is_flagged_on_its_button_and_the_summary_opens_the_group_to_focus_it()
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [
                FormFillClientTests.Form.Group("a", "Birinci", [Q("ad", "text", "Ad", required: "yes")], "compact"),
                FormFillClientTests.Form.Group("b", "İkinci", [Q("yas", "integer", "Yaş", required: "yes")], "compact"),
            ]),
        });
        await _site.OpenAsync(id);

        // Submitting goes to the first problem, which is inside a collapsed group: it is opened and the control focused.
        await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");
        await _site.WaitAsync("!document.getElementById('summary').hidden", "the summary");
        Assert.Equal("true", await _site.EvalAsync<string>("document.querySelector('[data-key=a] .collapse-toggle').getAttribute('aria-expanded')"));
        Assert.True(await _site.EvalAsync<bool>("document.activeElement === document.querySelector('[data-name=ad] input')"), "Submit did not reach the first problem.");

        // The other group is still collapsed and says it holds a problem, in its own name.
        Assert.Equal("false", await _site.EvalAsync<string>("document.querySelector('[data-key=b] .collapse-toggle').getAttribute('aria-expanded')"));
        Assert.False(await _site.EvalAsync<bool>("document.querySelector('[data-key=b] .collapse-flag').hidden"), "The collapsed group did not say it holds a problem.");
        Assert.Contains("needs attention", await _site.EvalAsync<string>("document.querySelector('[data-key=b] .collapse-toggle').textContent") ?? "");
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-key=a] .collapse-flag').hidden"));

        // The summary's link opens that group and focuses the question.
        await _site.EvalAsync<bool>("document.querySelectorAll('#summary a')[1].click(); true");
        Assert.Equal("true", await _site.EvalAsync<string>("document.querySelector('[data-key=b] .collapse-toggle').getAttribute('aria-expanded')"));
        Assert.True(await _site.EvalAsync<bool>("document.activeElement === document.querySelector('[data-name=yas] input')"), "The summary did not reach the question.");
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-key=b] .collapse-flag').hidden"));
    }

    [Fact]
    public async Task A_compact_repeats_items_start_collapsed_and_an_item_just_added_is_open()
    {
        JsonObject room = FormFillClientTests.Form.Repeat("oda", [Q("ad", "text", "Ad")]);
        room["appearance"] = new JsonArray("compact");
        room["labels"] = new JsonObject { [""] = "Oda" };
        string id = _site.Publish(new FormFillClientTests.Form { Root = FormFillClientTests.Form.Repeat("", [room]) });
        await _site.OpenAsync(id);

        // One item to start with, collapsed; the Remove button is still its own button beside the heading.
        Assert.Equal(1, await _site.EvalAsync<int>("document.querySelectorAll('.repeat-instance').length"));
        Assert.Equal("false", await _site.EvalAsync<string>("document.querySelector('.repeat-instance .collapse-toggle').getAttribute('aria-expanded')"));
        Assert.Contains("Oda 1", await _site.EvalAsync<string>("document.querySelector('.repeat-instance .collapse-toggle').textContent") ?? "");
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('.repeat-instance [data-focus=remove]') !== null"));

        await _site.EvalAsync<bool>("document.querySelector('[data-focus=add]').click(); true");
        await _site.WaitAsync("document.querySelectorAll('.repeat-instance').length === 2", "the second item");
        Assert.Equal("true", await _site.EvalAsync<string>("document.querySelectorAll('.repeat-instance .collapse-toggle')[1].getAttribute('aria-expanded')"));
        Assert.Equal("false", await _site.EvalAsync<string>("document.querySelectorAll('.repeat-instance .collapse-toggle')[0].getAttribute('aria-expanded')"));
        Assert.True(await _site.EvalAsync<bool>("document.activeElement === document.querySelectorAll('.repeat-instance')[1].querySelector('input')"), "Focus did not go to the new item.");
    }

    // -------------------------------------------------------------------------------------------------- autocomplete

    private static FormFillClientTests.Form Cities() => new()
    {
        Root = FormFillClientTests.Form.Repeat("", [
            Q("sehir", "select_one", "Şehir", "yes", "sehir", "autocomplete"),
        ]),
        Choices = Choices("sehir", Choice("ist", "İstanbul"), Choice("izm", "İzmir"), Choice("ank", "Ankara"), Choice("ada", "Adana"), Choice("ig", "Iğdır")),
    };

    [Fact]
    public async Task Autocomplete_is_a_combobox_that_narrows_its_listbox_as_text_is_typed()
    {
        await _site.OpenAsync(_site.Publish(Cities()));

        const string input = "[data-name=sehir] input[role=combobox]";
        Assert.Equal("listbox", await _site.EvalAsync<string>("document.querySelector('[data-name=sehir] ul').getAttribute('role')"));
        Assert.Equal("list", await _site.EvalAsync<string>($"document.querySelector('{input}').getAttribute('aria-autocomplete')"));
        Assert.Equal("false", await _site.EvalAsync<string>($"document.querySelector('{input}').getAttribute('aria-expanded')"));
        Assert.Equal(await _site.EvalAsync<string>("document.querySelector('[data-name=sehir] ul').id"), await _site.EvalAsync<string>($"document.querySelector('{input}').getAttribute('aria-controls')"));
        Assert.Equal("polite", await _site.EvalAsync<string>("document.querySelector('[data-name=sehir] .combo-status').getAttribute('aria-live')"));
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=sehir] ul').hidden"));

        // The label names it: the box is the question's own label's control.
        Assert.Equal("Şehir*", (await _site.EvalAsync<string>("document.querySelector('[data-name=sehir] label').textContent") ?? "").Trim());

        // "ist" contains-matches İstanbul only, with no regard to case, accent or the dotted i.
        await TypeAsync(input, "ist");
        Assert.Equal(["İstanbul"], await _site.EvalAsync<string[]>("Array.prototype.map.call(document.querySelectorAll('[data-name=sehir] [role=option]'), function (o) { return o.textContent; })") ?? []);
        Assert.Equal("true", await _site.EvalAsync<string>($"document.querySelector('{input}').getAttribute('aria-expanded')"));
        Assert.Contains("1 choice", await _site.EvalAsync<string>("document.querySelector('[data-name=sehir] .combo-status').textContent") ?? "");

        // "i" finds the three that contain an i of either kind: İstanbul, İzmir, Iğdır, and Adana has none; Ankara none.
        await TypeAsync(input, "i");
        Assert.Equal(["İstanbul", "İzmir", "Iğdır"], await _site.EvalAsync<string[]>("Array.prototype.map.call(document.querySelectorAll('[data-name=sehir] [role=option]'), function (o) { return o.textContent; })") ?? []);

        await TypeAsync(input, "zzz");
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=sehir] ul').hidden"));
        Assert.Contains("No choices match", await _site.EvalAsync<string>("document.querySelector('[data-name=sehir] .combo-status').textContent") ?? "");
    }

    [Fact]
    public async Task Autocomplete_is_chosen_from_with_the_arrows_and_enter_and_stores_the_choices_name()
    {
        string id = _site.Publish(Cities());
        await _site.OpenAsync(id);
        const string input = "[data-name=sehir] input[role=combobox]";

        await TypeAsync(input, "an");
        await KeyAsync(input, "ArrowDown");
        Assert.Equal("true", await _site.EvalAsync<string>("document.querySelector('[data-name=sehir] [role=option]').getAttribute('aria-selected')"));
        Assert.Equal(await _site.EvalAsync<string>("document.querySelector('[data-name=sehir] [role=option]').id"), await _site.EvalAsync<string>($"document.querySelector('{input}').getAttribute('aria-activedescendant')"));
        await KeyAsync(input, "ArrowDown");
        await KeyAsync(input, "Enter");

        // İstanbul, Ankara, Adana contain "an": the second is Ankara.
        Assert.Equal("Ankara", await _site.EvalAsync<string>($"document.querySelector('{input}').value"));
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=sehir] ul').hidden"));
        Assert.True(await _site.EvalAsync<bool>($"document.activeElement === document.querySelector('{input}')"), "Choosing dropped the focus.");
        Assert.Contains("selected", await _site.EvalAsync<string>("document.querySelector('[data-name=sehir] .combo-status').textContent") ?? "");

        await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");
        await _site.WaitAsync("!document.getElementById('done').hidden", "the confirmation");
        Assert.Contains(_site.Adds, a => a.Contains("\"sehir\":\"ank\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Autocomplete_goes_back_to_the_answer_when_the_box_is_left_with_text_that_names_nothing_and_escape_closes_it()
    {
        await _site.OpenAsync(_site.Publish(Cities()));
        const string input = "[data-name=sehir] input[role=combobox]";

        await TypeAsync(input, "ad");
        await KeyAsync(input, "ArrowDown");
        await KeyAsync(input, "Enter");
        Assert.Equal("Adana", await _site.EvalAsync<string>($"document.querySelector('{input}').value"));

        await TypeAsync(input, "xx");
        await KeyAsync(input, "Escape");
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=sehir] ul').hidden"));
        Assert.Equal("Adana", await _site.EvalAsync<string>($"document.querySelector('{input}').value"));

        // Leaving the box with text that names nothing puts the answer back.
        await TypeAsync(input, "qq");
        await BlurAsync(input);
        Assert.Equal("Adana", await _site.EvalAsync<string>($"document.querySelector('{input}').value"));

        // Emptying the box empties the answer.
        await TypeAsync(input, "");
        await BlurAsync(input);
        Assert.Equal(string.Empty, await _site.EvalAsync<string>($"document.querySelector('{input}').value"));
        await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");
        await _site.WaitAsync("!document.getElementById('summary').hidden", "the summary: the answer was cleared, and it is required");
    }

    [Fact]
    public async Task Autocomplete_follows_its_choice_filter_and_works_with_the_server_gone()
    {
        JsonObject region = Q("bolge", "select_one", "Bölge", list: "bolge");
        JsonObject city = Q("sehir", "select_one", "Şehir", null, "sehir", "autocomplete");
        city["choiceFilter"] = "bolge=${bolge}";
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [region, city]),
            Choices = new JsonObject
            {
                ["bolge"] = new JsonArray(Choice("ic", "İç Anadolu"), Choice("ege", "Ege")),
                ["sehir"] = new JsonArray(Choice("ank", "Ankara", "bolge", "ic"), Choice("izm", "İzmir", "bolge", "ege"), Choice("ayd", "Aydın", "bolge", "ege")),
            },
        });
        await _site.OpenAsync(id);
        const string input = "[data-name=sehir] input[role=combobox]";

        _site.Stop();
        try
        {
            await _site.EvalAsync<bool>("document.querySelector('[data-name=bolge] input[value=ege]').click(); true");
            await TypeAsync(input, "a");
            Assert.Equal(["Aydın"], await _site.EvalAsync<string[]>("Array.prototype.map.call(document.querySelectorAll('[data-name=sehir] [role=option]'), function (o) { return o.textContent; })") ?? []);
            await TypeAsync(input, "i");
            Assert.Equal(["İzmir", "Aydın"], await _site.EvalAsync<string[]>("Array.prototype.map.call(document.querySelectorAll('[data-name=sehir] [role=option]'), function (o) { return o.textContent; })") ?? []);
            await KeyAsync(input, "ArrowDown");
            await KeyAsync(input, "Enter");
            Assert.Equal("İzmir", await _site.EvalAsync<string>($"document.querySelector('{input}').value"));

            // Changing the region takes away an answer the filter no longer offers.
            await _site.EvalAsync<bool>("document.querySelector('[data-name=bolge] input[value=ic]').click(); true");
            await _site.WaitAsync($"document.querySelector('{input}').value === ''", "the answer the filter no longer offers to go");
        }
        finally
        {
            _site.Start();
        }
    }

    // ---------------------------------------------------------------------------------------------------- input mask

    private static FormFillClientTests.Form Masked(string mask, string? required = null) => new()
    {
        Root = FormFillClientTests.Form.Repeat("", [With(Q("tel", "text", "Telefon", required), "inputMask", mask)]),
    };

    [Fact]
    public async Task An_input_mask_puts_the_separators_in_as_digits_are_typed_and_drops_what_the_mask_does_not_take()
    {
        await _site.OpenAsync(_site.Publish(Masked("(999) 999-9999")));
        const string input = "[data-name=tel] input";

        Assert.Equal("(___) ___-____", await _site.EvalAsync<string>($"document.querySelector('{input}').getAttribute('placeholder')"));
        Assert.Contains("(___) ___-____", await _site.EvalAsync<string>("document.querySelector('[data-name=tel] .hint').textContent") ?? "");
        Assert.Contains("-format", await _site.EvalAsync<string>($"document.querySelector('{input}').getAttribute('aria-describedby')") ?? "");

        await TypeAsync(input, "5");
        Assert.Equal("(5", await _site.EvalAsync<string>($"document.querySelector('{input}').value"));
        await TypeAsync(input, "55a5123");
        Assert.Equal("(555) 123", await _site.EvalAsync<string>($"document.querySelector('{input}').value"));
        await TypeAsync(input, "555 123 4567 89");
        Assert.Equal("(555) 123-4567", await _site.EvalAsync<string>($"document.querySelector('{input}').value"));
        await TypeAsync(input, "(555) 123-4567");
        Assert.Equal("(555) 123-4567", await _site.EvalAsync<string>($"document.querySelector('{input}').value"));
        Assert.Equal("numeric", await _site.EvalAsync<string>($"document.querySelector('{input}').getAttribute('inputmode')"));
    }

    [Fact]
    public async Task An_input_masks_case_switches_and_letters_and_optional_places_are_honoured()
    {
        await _site.OpenAsync(_site.Publish(Masked(">AAA-<AAA-NNN;_")));
        const string input = "[data-name=tel] input";

        await TypeAsync(input, "abcDEF12");
        Assert.Equal("ABC-def-12", await _site.EvalAsync<string>($"document.querySelector('{input}').value"));
        Assert.Equal("___-___-___", await _site.EvalAsync<string>($"document.querySelector('{input}').getAttribute('placeholder')"));
    }

    [Fact]
    public async Task An_unfinished_answer_is_refused_at_submit_in_words_and_a_finished_one_is_sent_as_the_mask_shows_it()
    {
        string id = _site.Publish(Masked("999-99"));
        await _site.OpenAsync(id);
        const string input = "[data-name=tel] input";

        await TypeAsync(input, "12");
        await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");
        await _site.WaitAsync("!document.getElementById('summary').hidden", "the summary");
        Assert.Contains("Fill in the whole pattern: ___-__", await _site.EvalAsync<string>("document.querySelector('[data-name=tel] .error').textContent") ?? "");
        Assert.Equal("true", await _site.EvalAsync<string>($"document.querySelector('{input}').getAttribute('aria-invalid')"));
        Assert.True(await _site.EvalAsync<bool>("document.getElementById('done').hidden"), "An unfinished answer was sent.");

        await TypeAsync(input, "12345");
        await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");
        await _site.WaitAsync("!document.getElementById('done').hidden", "the confirmation");
        Assert.Contains(_site.Adds, a => a.Contains("\"tel\":\"123-45\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_separator_after_the_last_place_is_put_in_on_leaving_and_taken_off_again_when_deleted()
    {
        await _site.OpenAsync(_site.Publish(Masked("900 kg")));
        const string input = "[data-name=tel] input";

        await TypeAsync(input, "12");
        Assert.Equal("12", await _site.EvalAsync<string>($"document.querySelector('{input}').value"));
        await TypeAsync(input, "123");
        Assert.Equal("123 kg", await _site.EvalAsync<string>($"document.querySelector('{input}').value"));

        // A deletion that leaves the trailing text standing could never remove the last digit.
        await _site.EvalAsync<bool>($$"""
            (function () {
              var n = document.querySelector('{{input}}');
              n.focus();
              n.value = '123 k';
              n.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'deleteContentBackward' }));
              return true;
            })()
            """);
        Assert.Equal("123", await _site.EvalAsync<string>($"document.querySelector('{input}').value"));
    }

    // ---------------------------------------------------------------------------------------------------- spinner

    [Fact]
    public async Task A_spinner_steps_the_value_with_two_labelled_buttons_and_the_arrow_keys()
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [Q("adet", "integer", "Adet", null, null, "spinner"), Q("oran", "decimal", "Oran", null, null, "spinner")]),
        });
        await _site.OpenAsync(id);

        Assert.Equal(2, await _site.EvalAsync<int>("document.querySelectorAll('[data-name=adet] button').length"));
        Assert.Equal("Increase", await _site.EvalAsync<string>("document.querySelector('[data-name=adet] [data-focus=more]').getAttribute('aria-label')"));
        Assert.Equal("Decrease", await _site.EvalAsync<string>("document.querySelector('[data-name=adet] [data-focus=less]').getAttribute('aria-label')"));
        Assert.True(await _site.EvalAsync<bool>("Array.prototype.every.call(document.querySelectorAll('[data-name=adet] button'), function (b) { var r = b.getBoundingClientRect(); return r.width >= 44 && r.height >= 44; })"),
            "A step button is smaller than 44 px.");

        await _site.EvalAsync<bool>("document.querySelector('[data-name=adet] [data-focus=more]').click(); true");
        await _site.EvalAsync<bool>("document.querySelector('[data-name=adet] [data-focus=more]').click(); true");
        Assert.Equal("2", await _site.EvalAsync<string>("document.querySelector('[data-name=adet] input').value"));
        await _site.EvalAsync<bool>("var less = document.querySelector('[data-name=adet] [data-focus=less]'); less.focus(); less.click(); true");
        Assert.Equal("1", await _site.EvalAsync<string>("document.querySelector('[data-name=adet] input').value"));
        Assert.True(await _site.EvalAsync<bool>("document.activeElement === document.querySelector('[data-name=adet] [data-focus=less]')"), "Pressing a step button moved the focus.");

        await KeyAsync("[data-name=adet] input", "ArrowDown");
        await KeyAsync("[data-name=adet] input", "ArrowDown");
        Assert.Equal("-1", await _site.EvalAsync<string>("document.querySelector('[data-name=adet] input').value"));

        // A decimal steps by one and does not drift: 0.1 + 1 is 1.1, not 1.1000000000000001.
        await TypeAsync("[data-name=oran] input", "0.1");
        await _site.EvalAsync<bool>("document.querySelector('[data-name=oran] [data-focus=more]').click(); true");
        Assert.Equal("1.1", await _site.EvalAsync<string>("document.querySelector('[data-name=oran] input').value"));
    }

    // ------------------------------------------------------------------------------------------------ week number

    [Fact]
    public async Task A_week_number_is_a_year_and_a_week_stored_as_the_date_of_that_weeks_monday()
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [Q("hafta", "date", "Hafta", required: "yes", null, "week-number")]),
        });
        await _site.OpenAsync(id);

        Assert.Equal("group", await _site.EvalAsync<string>("document.querySelector('[data-name=hafta] .week-row').getAttribute('role')"));
        Assert.Equal(2, await _site.EvalAsync<int>("document.querySelectorAll('[data-name=hafta] input').length"));
        // Each box is named by the question and by its own word.
        Assert.Equal("Hafta* Year", (await _site.EvalAsync<string>("Array.prototype.map.call(document.querySelector('[data-name=hafta] input').getAttribute('aria-labelledby').split(' '), function (i) { return document.getElementById(i).textContent; }).join(' ')") ?? "").Trim());

        // ISO 8601: 2026 starts on a Thursday, so its week 1 begins on Monday 29 December 2025, and it has 53 weeks.
        await TypeAsync("[data-name=hafta] input[data-focus=year]", "2026");
        await TypeAsync("[data-name=hafta] input[data-focus=week]", "1");
        await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");
        await _site.WaitAsync("!document.getElementById('done').hidden", "the confirmation");
        long monday = new DateTimeOffset(new DateTime(2025, 12, 29, 0, 0, 0, DateTimeKind.Local)).ToUnixTimeMilliseconds();
        Assert.Contains(_site.Adds, a => a.Contains($"\"hafta\":{monday}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_week_that_does_not_exist_or_is_half_entered_is_refused_in_words_and_a_stored_date_shows_its_week()
    {
        JsonObject week = Q("hafta", "date", "Hafta", null, null, "week-number");
        week["default"] = "2026-03-02";
        string id = _site.Publish(new FormFillClientTests.Form { Root = FormFillClientTests.Form.Repeat("", [week]) });
        await _site.OpenAsync(id);

        Assert.Equal("2026", await _site.EvalAsync<string>("document.querySelector('[data-name=hafta] input[data-focus=year]').value"));
        Assert.Equal("10", await _site.EvalAsync<string>("document.querySelector('[data-name=hafta] input[data-focus=week]').value"));

        // 2027 has 52 weeks.
        await TypeAsync("[data-name=hafta] input[data-focus=year]", "2027");
        await TypeAsync("[data-name=hafta] input[data-focus=week]", "53");
        await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");
        await _site.WaitAsync("!document.getElementById('summary').hidden", "the summary");
        Assert.Contains("week number that exists", await _site.EvalAsync<string>("document.querySelector('[data-name=hafta] .error').textContent") ?? "");
        // What was typed is still there: the box was not emptied under the person's hands.
        Assert.Equal("53", await _site.EvalAsync<string>("document.querySelector('[data-name=hafta] input[data-focus=week]').value"));
    }

    // --------------------------------------------------------------------------------------- or_other, duplicates

    [Fact]
    public async Task The_other_choice_shows_the_text_question_that_follows_and_a_choice_filter_does_not_take_it_away()
    {
        JsonObject colour = Q("renk", "select_one", "Renk", list: "renk");
        colour["choiceFilter"] = "grup=${grup}";
        JsonObject specify = Q("renk_other", "text", "Specify other");
        specify["relevant"] = "selected(${renk}, 'other')";
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [Q("grup", "text", "Grup"), colour, specify]),
            Choices = Choices("renk", Choice("kirmizi", "Kırmızı", "grup", "sicak"), Choice("mavi", "Mavi", "grup", "soguk"), Choice("other", "Other", "grup", "", auto: true)),
        });
        await _site.OpenAsync(id);

        await TypeAsync("[data-name=grup] input", "sicak");
        Assert.Equal(["kirmizi", "other"], await _site.EvalAsync<string[]>("Array.prototype.map.call(document.querySelectorAll('[data-name=renk] input'), function (i) { return i.value; })") ?? []);
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=renk_other]').hidden"));

        await _site.EvalAsync<bool>("document.querySelector('[data-name=renk] input[value=other]').click(); true");
        Assert.False(await _site.EvalAsync<bool>("document.querySelector('[data-name=renk_other]').hidden"));
        await TypeAsync("[data-name=renk_other] input", "Turuncu");
        await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");
        await _site.WaitAsync("!document.getElementById('done').hidden", "the confirmation");
        Assert.Contains(_site.Adds, a => a.Contains("\"renk\":\"other\"", StringComparison.Ordinal) && a.Contains("\"renk_other\":\"Turuncu\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_choice_named_twice_is_shown_chosen_once_at_its_first_place()
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [Q("renk", "select_one", "Renk", list: "renk")]),
            Choices = Choices("renk", Choice("k", "Kırmızı"), Choice("m", "Mavi"), Choice("k", "Koyu kırmızı")),
        });
        await _site.OpenAsync(id);

        Assert.Equal(3, await _site.EvalAsync<int>("document.querySelectorAll('[data-name=renk] input').length"));
        await _site.EvalAsync<bool>("document.querySelectorAll('[data-name=renk] input')[2].click(); true");
        bool[] checkedNow = await _site.EvalAsync<bool[]>("Array.prototype.map.call(document.querySelectorAll('[data-name=renk] input'), function (i) { return i.checked; })") ?? [];
        Assert.Equal([true, false, false], checkedNow);
        await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");
        await _site.WaitAsync("!document.getElementById('done').hidden", "the confirmation");
        Assert.Contains(_site.Adds, a => a.Contains("\"renk\":\"k\"", StringComparison.Ordinal));
    }
}
