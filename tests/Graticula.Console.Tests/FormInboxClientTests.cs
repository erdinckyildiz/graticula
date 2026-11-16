using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;
using static Graticula.Console.Tests.FormFillClientTests;

namespace Graticula.Console.Tests;

/// <summary>
/// The form client's Inbox — ADR-300 §5.6–§5.8: records downloaded, opened offline, changed and sent with the EditDate
/// they were read at; a stale change shown beside the server's version; a record gone from the server kept on the device.
/// </summary>
/// <remarks>
/// Against <see cref="FormFillClientTests.Site"/>, whose stand-in Inbox view answers as ADR-300's server does: a row that
/// moved since its <c>expectedEditDates</c> is refused with 412 in its result, one that is gone with 404. What the real
/// server does with the parameter is tested on its side; what is tested here is that the page sends it, and what the
/// page does with each answer.
/// </remarks>
public sealed class FormInboxClientTests : IClassFixture<FormFillClientTests.Site>
{
    private static readonly long Day = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 1))).ToUnixTimeMilliseconds();

    private readonly Site _site;

    /// <summary>Creates the tests over the site.</summary>
    /// <param name="site">The stand-in server and the browser.</param>
    public FormInboxClientTests(Site site) => _site = site;

    // ----------------------------------------------------------------------------------------------------- the tests

    [Fact]
    public async Task Refresh_downloads_the_records_and_they_open_with_no_server()
    {
        _site.SignedIn = true;
        string id = _site.Publish(Inspection());
        await _site.OpenAsync(id);
        await _site.WaitAsync("!!navigator.serviceWorker.controller", "the service worker to take the page", seconds: 20);
        await _site.OpenAsync(id);

        await RefreshAsync(2);
        Assert.Contains("Downloaded 2 records", await _site.EvalAsync<string>("document.getElementById('inbox-status').textContent"));
        Assert.Equal("status", await _site.EvalAsync<string>("document.getElementById('inbox-status').getAttribute('role')"));
        Assert.Contains("Last refreshed", await _site.EvalAsync<string>("document.getElementById('inbox-refreshed').textContent"));
        string[] labels = await _site.EvalAsync<string[]>("Array.prototype.map.call(document.querySelectorAll('#inbox-list li strong'), function (n) { return n.textContent; })") ?? [];
        Assert.Contains("Ayşe · Açık", labels);
        Assert.Contains("Mehmet · Kapalı", labels);

        // The search narrows the list as it is typed, and the box keeps the focus while the list is drawn again.
        await _site.EvalAsync<bool>("""
            (function () {
              var box = document.getElementById('inbox-search');
              box.focus();
              box.value = 'mehmet';
              box.dispatchEvent(new Event('input', { bubbles: true }));
              return true;
            })()
            """);
        await _site.WaitAsync("document.querySelectorAll('#inbox-list li[data-key]').length === 1", "the search to narrow the list");
        Assert.Equal("inbox-search", await _site.EvalAsync<string>("document.activeElement.id"));
        Assert.Contains("1 of 2", await _site.EvalAsync<string>("document.getElementById('inbox-shown').textContent"));

        _site.Stop();
        try
        {
            await _site.OpenAsync(id, expectServer: false);
            await _site.WaitAsync("!document.getElementById('inbox-toggle').hidden", "the Inbox to be offered with no server", seconds: 20);
            await _site.EvalAsync<bool>("document.getElementById('inbox-toggle').click(); true");
            await _site.WaitAsync("document.querySelectorAll('#inbox-list li[data-key]').length === 2", "the records kept on the device");
            await OpenRecordAsync("Ayşe");

            Assert.Equal("Ayşe", await _site.EvalAsync<string>("document.querySelector('[data-name=ad] input').value"));
            Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=durum] input[value=acik]').checked"), "The choice was not filled in.");
            Assert.Equal("2026-11-01", await _site.EvalAsync<string>("document.querySelector('[data-name=tarih] input').value"));
            Assert.Equal("41.01", await _site.EvalAsync<string>("document.querySelector('[data-name=konum] input[data-focus=lat]').value"));
            Assert.Equal(1, await _site.EvalAsync<int>("document.querySelectorAll('[data-key=oda] > .repeat-instance').length"));
            Assert.Equal("12.5", await _site.EvalAsync<string>("document.querySelector('[data-name=alan] input').value"));
            Assert.False(await _site.EvalAsync<bool>("document.getElementById('inbox-editing').hidden"), "Nothing says a record from the Inbox is open.");
            Assert.True(await _site.EvalAsync<bool>("document.getElementById('form').contains(document.activeElement)"), "Focus was left outside the form after opening a record.");
        }
        finally
        {
            _site.Start();
        }
    }

    [Fact]
    public async Task A_change_is_sent_to_the_inbox_view_with_the_edit_date_it_was_read_at()
    {
        _site.SignedIn = true;
        string id = _site.Publish(Inspection());
        long read = EditDate(id, "", 1);
        await _site.OpenAsync(id);
        await RefreshAsync(2);
        await OpenRecordAsync("Ayşe");

        // The picture the server holds is shown and cannot be removed; a new one can be added.
        await _site.WaitAsync("document.querySelectorAll('[data-name=foto] .existing-files li').length === 1", "the existing picture to be listed");
        Assert.Contains("eski.png", await _site.EvalAsync<string>("document.querySelector('[data-name=foto] .existing-files').textContent"));
        Assert.Equal(0, await _site.EvalAsync<int>("document.querySelectorAll('[data-name=foto] .existing-files button').length"));
        await _site.EvalAsync<bool>("""
            (function () {
              var input = document.querySelector('[data-name=foto] input[type=file]');
              var bytes = Uint8Array.from(atob('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=='), function (c) { return c.charCodeAt(0); });
              var dt = new DataTransfer();
              dt.items.add(new File([bytes], 'yeni.png', { type: 'image/png' }));
              input.files = dt.files;
              input.dispatchEvent(new Event('change', { bubbles: true }));
              return true;
            })()
            """);

        await _site.SetAsync("ad", "Ayşe Kaya");
        await SubmitAsync();
        await _site.WaitAsync("!document.getElementById('done').hidden", "the confirmation");
        Assert.Contains("Your changes were sent", await _site.EvalAsync<string>("document.getElementById('done-text').textContent"));
        Assert.True(await _site.EvalAsync<bool>("document.getElementById('inbox-editing').hidden"), "The line about the record being changed stayed after the form went.");

        InboxData inbox = _site.InboxOf(id);
        lock (inbox)
        {
            JsonObject sent = inbox.Edits.First(e => (string?)e["table"] == "" && e["updates"]!.AsArray().Count > 0);
            JsonObject update = sent["updates"]![0]!["attributes"]!.AsObject();
            Assert.Equal(1, (int)update["objectid"]!);
            Assert.Equal("Ayşe Kaya", (string?)update["ad"]);
            Assert.Equal(read, (long)sent["expected"]!["1"]!);
            Assert.Equal("Ayşe Kaya", (string?)inbox.Row("", 1)["attributes"]!["ad"]);
            Assert.Contains(inbox.AttachmentsAdded, a => a.StartsWith("|1|", StringComparison.Ordinal) && !a.Contains("parentGlobalId", StringComparison.Ordinal));
        }

        Assert.Empty(await OutboxAsync(id));
    }

    [Fact]
    public async Task A_stale_change_shows_both_versions_and_keep_mine_sends_it_against_the_new_edit_date()
    {
        _site.SignedIn = true;
        string id = _site.Publish(Inspection());
        await _site.OpenAsync(id);
        await RefreshAsync(2);
        await OpenRecordAsync("Ayşe");
        _site.ChangeOnServer(id, "", 1, "ad", "Sunucudaki");
        long moved = EditDate(id, "", 1);

        await _site.SetAsync("ad", "Benimki");
        await SubmitAsync();
        await _site.WaitAsync("!document.getElementById('compare').hidden", "both versions to be shown");
        Assert.True(await _site.EvalAsync<bool>("document.getElementById('compare').contains(document.activeElement)"), "Focus did not move to the comparison.");
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('#compare tr[data-question=ad]').classList.contains('differs')"), "The answer that differs is not marked.");
        Assert.Contains("differs", await _site.EvalAsync<string>("document.querySelector('#compare tr[data-question=ad] th').textContent"));
        string[] cells = await _site.EvalAsync<string[]>("Array.prototype.map.call(document.querySelectorAll('#compare tr[data-question=ad] td'), function (c) { return c.textContent; })") ?? [];
        Assert.Equal(["Benimki", "Sunucudaki"], cells);
        Assert.False(await _site.EvalAsync<bool>("document.querySelector('#compare tr[data-question=durum]').classList.contains('differs')"), "An answer that is the same is marked as differing.");
        Assert.Equal("conflict", (await OutboxAsync(id)).Single());

        await _site.EvalAsync<bool>("document.getElementById('compare-mine').click(); true");
        await _site.WaitAsync("document.getElementById('compare-status').textContent.indexOf('Your version was sent') >= 0", "the change to be sent again");
        Assert.Equal("compare-close", await _site.EvalAsync<string>("document.activeElement.id"));

        InboxData inbox = _site.InboxOf(id);
        lock (inbox)
        {
            Assert.Equal("Benimki", (string?)inbox.Row("", 1)["attributes"]!["ad"]);
            JsonObject last = inbox.Edits.Last(e => (string?)e["table"] == "" && e["updates"]!.AsArray().Count > 0);
            Assert.Equal(moved, (long)last["expected"]!["1"]!);
        }

        Assert.Empty(await OutboxAsync(id));
    }

    /// <summary>
    /// Found by the ux review 2026-11-16: <i>Keep mine</i> sent every answer again, and the office's change to an answer the
    /// member never touched went back to what the device had downloaded.
    /// </summary>
    [Fact]
    public async Task Keep_mine_keeps_the_servers_change_to_an_answer_the_member_did_not_touch()
    {
        _site.SignedIn = true;
        string id = _site.Publish(Inspection());
        await _site.OpenAsync(id);
        await RefreshAsync(2);
        await OpenRecordAsync("Ayşe");
        _site.ChangeOnServer(id, "", 1, "durum", "kapali");

        await _site.SetAsync("ad", "Benimki");
        await SubmitAsync();
        await _site.WaitAsync("!document.getElementById('compare').hidden", "both versions to be shown");
        await _site.EvalAsync<bool>("document.getElementById('compare-mine').click(); true");
        await _site.WaitAsync("document.getElementById('compare-status').textContent.indexOf('Your version was sent') >= 0", "the change to be sent again");

        InboxData inbox = _site.InboxOf(id);
        lock (inbox)
        {
            JsonNode attributes = inbox.Row("", 1)["attributes"]!;
            Assert.Equal("Benimki", (string?)attributes["ad"]);
            Assert.Equal("kapali", (string?)attributes["durum"]);
        }
    }

    /// <summary>
    /// Owner decision 2026-11-16 (ADR-300 §5.12): <i>Use the server's</i> gives way only where the server changed an answer;
    /// the member's change to an answer the server never touched is still sent.
    /// </summary>
    [Fact]
    public async Task Use_the_servers_gives_way_only_where_the_server_changed_an_answer()
    {
        _site.SignedIn = true;
        string id = _site.Publish(Inspection());
        await _site.OpenAsync(id);
        await RefreshAsync(2);
        await OpenRecordAsync("Ayşe");
        _site.ChangeOnServer(id, "", 1, "durum", "kapali");

        await _site.SetAsync("ad", "Benimki");
        await SubmitAsync();
        await _site.WaitAsync("!document.getElementById('compare').hidden", "both versions to be shown");
        await _site.EvalAsync<bool>("window.confirm = function () { return true; }; document.getElementById('compare-server').click(); true");
        await _site.WaitAsync("document.getElementById('compare-status').textContent.indexOf('your other changes were sent') >= 0", "the member's other change to be sent");

        InboxData inbox = _site.InboxOf(id);
        lock (inbox)
        {
            JsonNode attributes = inbox.Row("", 1)["attributes"]!;
            Assert.Equal("Benimki", (string?)attributes["ad"]);
            Assert.Equal("kapali", (string?)attributes["durum"]);
        }

        Assert.Empty(await OutboxAsync(id));
    }

    [Fact]
    public async Task A_stale_change_can_give_way_to_the_servers_version_and_nothing_of_it_is_sent()
    {
        _site.SignedIn = true;
        string id = _site.Publish(Inspection());
        await _site.OpenAsync(id);
        await RefreshAsync(2);
        await OpenRecordAsync("Ayşe");
        _site.ChangeOnServer(id, "", 1, "ad", "Sunucudaki");

        await _site.SetAsync("ad", "Benimki");
        await SubmitAsync();
        await _site.WaitAsync("!document.getElementById('compare').hidden", "both versions to be shown");
        await _site.EvalAsync<bool>("window.confirm = function () { return true; }; document.getElementById('compare-server').click(); true");
        await _site.WaitAsync("document.getElementById('compare-status').textContent.indexOf('version was kept') >= 0", "the server's version to be kept");

        InboxData inbox = _site.InboxOf(id);
        lock (inbox)
        {
            Assert.Equal("Sunucudaki", (string?)inbox.Row("", 1)["attributes"]!["ad"]);
            Assert.Single(inbox.Edits, e => (string?)e["table"] == "" && e["updates"]!.AsArray().Count > 0);
        }

        Assert.Empty(await OutboxAsync(id));

        // The record on the device is the server's now, so the next change is made against it.
        await _site.WaitAsync($$"""
            {{Site.IdbAll("inbox")}}.then(function (all) {
              var e = all.filter(function (x) { return x.formId === '{{id}}' && x.kind === 'record' && x.objectId === 1; })[0];
              return !!e && e.attributes.ad === 'Sunucudaki';
            })
            """, "the record on the device to take the server's version");
    }

    [Fact]
    public async Task A_record_gone_from_the_server_is_kept_on_the_device_with_its_answers_readable()
    {
        _site.SignedIn = true;
        string id = _site.Publish(Inspection());
        await _site.OpenAsync(id);
        await RefreshAsync(2);
        await OpenRecordAsync("Ayşe");
        _site.DeleteOnServer(id, "", 1);

        await _site.SetAsync("ad", "Kaybolmasın");
        await SubmitAsync();
        await _site.WaitAsync("!document.getElementById('done').hidden", "the screen after sending");
        Assert.Contains("no longer on the server", await _site.EvalAsync<string>("document.getElementById('done-text').textContent"));
        Assert.Equal("gone", (await OutboxAsync(id)).Single());

        // Listed in the Outbox with its answers, and marked in the Inbox; a refresh does not take it off the device.
        await _site.EvalAsync<bool>("document.getElementById('outbox-toggle').click(); true");
        await _site.WaitAsync("!!document.querySelector('#outbox-list [data-focus^=answers-]')", "the Outbox to offer the answers");
        await _site.EvalAsync<bool>("document.querySelector('#outbox-list [data-focus^=answers-]').click(); true");
        await _site.WaitAsync("!document.getElementById('answers-view').hidden", "the answers to be shown");
        string answers = await _site.EvalAsync<string>("document.getElementById('answers-list').textContent") ?? string.Empty;
        Assert.Contains("Kaybolmasın", answers);
        Assert.Contains("Açık", answers);
        Assert.Equal("answers-heading", await _site.EvalAsync<string>("document.activeElement.id"));
        Assert.False(await _site.EvalAsync<bool>("document.getElementById('answers-copy').hidden"), "There is no way to copy the answers.");

        await _site.EvalAsync<bool>("document.getElementById('answers-close').click(); true");
        await RefreshAsync(2);
        Assert.Contains("No longer on the server", await _site.EvalAsync<string>("document.getElementById('inbox-list').textContent"));
    }

    [Fact]
    public async Task An_anonymous_caller_is_not_offered_the_inbox()
    {
        _site.SignedIn = false;
        string id = _site.Publish(Inspection());
        await _site.OpenAsync(id);
        Assert.True(await _site.EvalAsync<bool>("document.getElementById('inbox-toggle').hidden"), "The Inbox is offered to an anonymous caller.");
        Assert.True(await _site.EvalAsync<bool>("document.getElementById('inbox').hidden"));
    }

    [Fact]
    public async Task Existing_repeat_rows_change_only_where_the_repeat_allows_updates_and_new_rows_are_added()
    {
        _site.SignedIn = true;
        string id = _site.Publish(Inspection());
        long roomRead = EditDate(id, "oda", 1);
        await _site.OpenAsync(id);
        await RefreshAsync(2);
        await OpenRecordAsync("Ayşe");

        // `oda` allows updates; `notlar` does not. Neither offers to remove a row the server holds.
        Assert.False(await _site.EvalAsync<bool>("document.querySelector('[data-name=alan] input').readOnly"), "A row of a repeat that allows updates cannot be changed.");
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=metin] input').readOnly"), "A row of a repeat that does not allow updates can be changed.");
        Assert.Equal(0, await _site.EvalAsync<int>("document.querySelectorAll('[data-key=oda] > .repeat-instance [data-focus=remove], [data-key=notlar] > .repeat-instance [data-focus=remove]').length"));

        await _site.SetAsync("alan", "20");
        await _site.EvalAsync<bool>("document.querySelector('[data-key=notlar] > .inline > button').click(); true");
        await _site.WaitAsync("document.querySelectorAll('[data-key=notlar] > .repeat-instance').length === 2", "a new row");
        await _site.EvalAsync<bool>("""
            (function () {
              var inputs = document.querySelectorAll('[data-key=notlar] > .repeat-instance [data-name=metin] input');
              var input = inputs[inputs.length - 1];
              input.focus();
              input.value = 'yeni not';
              input.dispatchEvent(new Event('input', { bubbles: true }));
              return true;
            })()
            """);
        await SubmitAsync();
        await _site.WaitAsync("!document.getElementById('done').hidden", "the confirmation");

        InboxData inbox = _site.InboxOf(id);
        lock (inbox)
        {
            JsonObject rooms = inbox.Edits.Single(e => (string?)e["table"] == "oda" && e["updates"]!.AsArray().Count > 0);
            Assert.Equal(20, (double)rooms["updates"]![0]!["attributes"]!["alan"]!);
            Assert.Equal(roomRead, (long)rooms["expected"]!["1"]!);
            Assert.DoesNotContain(inbox.Edits, e => (string?)e["table"] == "notlar" && e["updates"]!.AsArray().Count > 0);
            JsonObject notes = inbox.Edits.Single(e => (string?)e["table"] == "notlar" && e["adds"]!.AsArray().Count > 0);
            JsonObject added = notes["adds"]![0]!["attributes"]!.AsObject();
            Assert.Equal("yeni not", (string?)added["metin"]);
            Assert.Equal((string?)inbox.Row("", 1)["attributes"]!["globalid"], (string?)added["parentglobalid"]);
            Assert.Equal(2, inbox.Rows["notlar"].Count);
            Assert.Equal("eski not", (string?)inbox.Row("notlar", 1)["attributes"]!["metin"]);
        }
    }

    [Fact]
    public async Task On_a_phone_the_inbox_and_the_comparison_fit_and_every_control_can_be_reached()
    {
        _site.SignedIn = true;
        string id = _site.Publish(Inspection());
        await _site.Browser.CallAsync("Emulation.setDeviceMetricsOverride", new { width = 360, height = 740, deviceScaleFactor = 2, mobile = true });

        try
        {
            await _site.OpenAsync(id);
            await RefreshAsync(2);
            await ShotAsync("inbox-360");
            Assert.Equal(string.Empty, await _site.EvalAsync<string>(Unreachable("#inbox-toggle, #inbox button, #inbox input")));

            await OpenRecordAsync("Ayşe");
            _site.ChangeOnServer(id, "", 1, "ad", "Sunucuda yazılan uzun bir ad, telefonda satıra sığmayacak kadar uzun");
            await _site.SetAsync("ad", "Benimki");
            await SubmitAsync();
            await _site.WaitAsync("!document.getElementById('compare').hidden", "both versions to be shown");
            await ShotAsync("compare-360");
            Assert.Equal(string.Empty, await _site.EvalAsync<string>(Unreachable("#compare button:not([hidden])")));
        }
        finally
        {
            await _site.Browser.CallAsync("Emulation.clearDeviceMetricsOverride");
        }
    }

    // --------------------------------------------------------------------------------------------------- the helpers

    /// <summary>
    /// JavaScript naming every control matched that is drawn outside the 360 px screen, or not drawn at all, and the page
    /// if it scrolls sideways; empty when everything can be reached.
    /// </summary>
    private static string Unreachable(string selector) => $$"""
        (function () {
          var bad = Array.prototype.filter.call(document.querySelectorAll({{JsonSerializer.Serialize(selector)}}), function (n) {
            if (n.closest('[hidden]')) { return false; }
            var r = n.getBoundingClientRect();
            return r.width < 1 || r.height < 1 || r.left < 0 || r.right > document.documentElement.clientWidth + 0.5;
          }).map(function (n) { return n.id || n.textContent.trim().slice(0, 30) || n.outerHTML.slice(0, 60); });
          if (document.documentElement.scrollWidth > document.documentElement.clientWidth) { bad.push('the page scrolls sideways (' + document.documentElement.scrollWidth + ' px)'); }
          return bad.join('; ');
        })()
        """;

    /// <summary>A picture of the page, kept when <c>GRATICULA_TEST_SHOTS</c> names a folder, for a person to look at.</summary>
    private async Task ShotAsync(string name)
    {
        string? folder = Environment.GetEnvironmentVariable("GRATICULA_TEST_SHOTS");

        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        JsonElement shot = await _site.Browser.CallAsync("Page.captureScreenshot", new { format = "png", captureBeyondViewport = true });
        System.IO.Directory.CreateDirectory(folder);
        await System.IO.File.WriteAllBytesAsync(System.IO.Path.Combine(folder, name + ".png"), Convert.FromBase64String(shot.GetProperty("data").GetString()!));
    }

    /// <summary>
    /// An inspection form: a name, a choice, a date, a place and a photo; a repeat of rooms that allows updates and one
    /// of notes that does not. Two records, Ayşe's with a room, a note and a photo, and Mehmet's.
    /// </summary>
    private static Form Inspection()
    {
        JsonObject rooms = Form.Repeat("oda", [Form.Question("alan", "decimal", "Alan")]);
        rooms["parameters"] = new JsonObject { ["allowUpdates"] = "true" };
        JsonObject notes = Form.Repeat("notlar", [Form.Question("metin", "text", "Metin")]);
        InboxData inbox = new() { Limit = 50 };
        Form form = new()
        {
            Root = Form.Repeat("", [
                Form.Question("ad", "text", "Ad"),
                Form.Question("durum", "select_one", "Durum", list: "durumlar"),
                Form.Question("tarih", "date", "Tarih"),
                Form.Question("konum", "geopoint", "Konum"),
                Form.Question("foto", "image", "Fotoğraf"),
                rooms,
                notes,
            ]),
            Choices = new JsonObject { ["durumlar"] = new JsonArray(Form.Choice("acik", "Açık"), Form.Choice("kapali", "Kapalı")) },
            Inbox = inbox,
        };

        JsonObject first = inbox.AddRow("", new JsonObject { ["ad"] = "Ayşe", ["durum"] = "acik", ["tarih"] = Day }, new JsonObject { ["x"] = 29.0, ["y"] = 41.01 });
        inbox.AddRow("", new JsonObject { ["ad"] = "Mehmet", ["durum"] = "kapali" }, new JsonObject { ["x"] = 29.1, ["y"] = 41.02 });
        string parent = (string)first["attributes"]!["globalid"]!;
        inbox.AddRow("oda", new JsonObject { ["alan"] = 12.5, ["parentglobalid"] = parent });
        inbox.AddRow("notlar", new JsonObject { ["metin"] = "eski not", ["parentglobalid"] = parent });
        inbox.Attachments["|1"] = [new JsonObject { ["id"] = 7, ["name"] = "eski.png", ["contentType"] = "image/png", ["size"] = 68, ["keywords"] = "foto" }];
        return form;
    }

    private long EditDate(string id, string table, int objectId)
    {
        InboxData inbox = _site.InboxOf(id);

        lock (inbox)
        {
            return (long)inbox.Row(table, objectId)["attributes"]!["last_edited_date"]!;
        }
    }

    /// <summary>Opens the Inbox, presses Refresh and waits for the records.</summary>
    private async Task RefreshAsync(int expected)
    {
        await _site.WaitAsync("!document.getElementById('inbox-toggle').hidden", "the Inbox to be offered");
        await _site.EvalAsync<bool>("if (document.getElementById('inbox').hidden) { document.getElementById('inbox-toggle').click(); } true");
        await _site.EvalAsync<bool>("document.getElementById('inbox-status').textContent = ''; document.getElementById('inbox-refresh').click(); true");
        await _site.WaitAsync($"document.getElementById('inbox-status').textContent.length > 0 && !document.getElementById('inbox-refresh').disabled && document.querySelectorAll('#inbox-list li[data-key]').length === {expected}",
            $"{expected} records to be downloaded");
    }

    /// <summary>Opens the record whose label starts with the given text.</summary>
    private async Task OpenRecordAsync(string label)
    {
        await _site.EvalAsync<bool>($$"""
            (function () {
              var item = Array.prototype.filter.call(document.querySelectorAll('#inbox-list li[data-key]'), function (li) {
                return li.querySelector('strong').textContent.indexOf({{JsonSerializer.Serialize(label)}}) === 0;
              })[0];
              item.querySelector('[data-focus^=open-]').click();
              return true;
            })()
            """);
        await _site.WaitAsync($"document.querySelector('[data-name=ad] input').value === {JsonSerializer.Serialize(label)}", $"{label}'s record to be opened");
    }

    private async Task SubmitAsync() =>
        await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");

    /// <summary>The statuses of one form's records waiting on the device.</summary>
    private async Task<string[]> OutboxAsync(string id) =>
        await _site.EvalAsync<string[]>($"{Site.IdbAll("outbox")}.then(function (all) {{ return all.filter(function (r) {{ return r.formId === '{id}'; }}).map(function (r) {{ return r.status; }}); }})") ?? [];
}
