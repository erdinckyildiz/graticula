using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Webhooks in Studio — ADR-266 condition 2: Organization › Settings › Webhooks lists the organization's webhooks with
/// Create, Activate, Deactivate, Delete and each one's recent deliveries, and a feature service's Settings › Webhooks
/// lists its own with the same.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asserted against the server, not against what was pressed.</b> Each step reads the webhook back through the API
/// the screen writes to, and a delivery is only looked for once a real receiver in this process has been sent one: a
/// row that says Delivered while nothing arrived would pass a test that only read the screen.
/// </para>
/// <para>
/// <b>First run, and the reader who may not.</b> The organization's list is opened empty first, and a reader without
/// <c>admin:manageSecurity</c> is shown neither the column's entry nor a control that writes. Visibility is
/// <c>getClientRects</c>, which a closed <c>details</c> or <c>display: none</c> defeats where <c>offsetParent</c> does not.
/// </para>
/// <para>
/// Every webhook, item and feature made here is removed in <c>finally</c>.
/// </para>
/// </remarks>
public sealed class WebhooksScreenTests : ConsoleTest
{
    private const string Portal = "/sharing/rest/portals/self/webhooks";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);

    private static string User => Environment.GetEnvironmentVariable(UserVariable)
        ?? throw new InvalidOperationException($"{UserVariable} is not set; this test signs in as that user.");

    private async Task<JsonElement> PostAsync(string path, Dictionary<string, string>? form = null)
    {
        (string token, _) = await SignInAsync();
        form ??= [];
        form["f"] = "json";
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{Root}{path}")) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(body) ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<JsonElement> GetAsync(string path)
    {
        (int status, string body) = await AdminAsync(HttpMethod.Get, path);
        Assert.True(status == 200, $"GET {path} answered {status}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<JsonElement[]> OrganizationsWebhooksAsync() =>
        [.. (await GetAsync($"{Portal}?f=json&num=100")).GetProperty("webhooks").EnumerateArray()];

    private async Task<JsonElement[]> ServicesWebhooksAsync(string admin) =>
        [.. (await GetAsync($"{admin}?f=json")).EnumerateArray()];

    /// <summary>Lets this screen's own writes reach the server; the harness answers every other write itself.</summary>
    private async Task LetWritesAsync() =>
        await Browser.EvaluateAsync<bool>("(() => { window.__letWritesTo = ['/webhooks']; return true; })()");

    private async Task TypeAsync(string id, string value) =>
        await Browser.EvaluateAsync<bool>(
            $"(() => {{ const e = document.getElementById({JsonSerializer.Serialize(id)}); e.value = {JsonSerializer.Serialize(value)}; return true; }})()");

    private async Task<string> TextAsync(string expression) => await Browser.EvaluateAsync<string>(expression) ?? string.Empty;

    private async Task ConfirmAsync(string mustSay)
    {
        await WaitForAsync("document.getElementById('serverConfirm')?.open", "Delete asked nothing.");
        Assert.Contains(mustSay, await TextAsync("document.getElementById('serverConfirmTitle').textContent + ' ' + document.getElementById('serverConfirmBody').textContent"), StringComparison.Ordinal);
        await ClickAsync("#serverConfirmGo");
    }

    /// <summary>Opens a row's ⋯ as a reader does, waits for the item to be shown, and presses it.</summary>
    private async Task MenuAsync(string scope, string id, string act)
    {
        string summary = $"[data-wh-act=menu][data-wh-scope={scope}][data-wh-id='{id}']";
        string item = $"[data-wh-act={act}][data-wh-scope={scope}][data-wh-id='{id}']";
        await ClickAsync(summary);
        await WaitForAsync(Shown(item), $"The row's ⋯ did not show its {act}.");
        await ClickAsync(item);
    }

    /// <summary>Whether an element's box lies inside the window, top and bottom.</summary>
    private static string InView(string id) =>
        $"(() => {{ const r = document.getElementById('{id}')?.getBoundingClientRect(); "
        + "return !!r && r.height > 0 && r.top >= 0 && r.bottom <= window.innerHeight; })()";

    /// <summary>
    /// Opens the row's deliveries and presses Refresh until the region says Delivered with the receiver's 200: a delivery
    /// is sent after the event commits, so the first read may still find it waiting.
    /// </summary>
    private async Task DeliveredAsync(string scope, string id)
    {
        string button = $"[data-wh-act=deliveries][data-wh-scope={scope}][data-wh-id='{id}']";
        await ClickAsync(button);
        await WaitForAsync($"!!document.getElementById('whSent-{scope}')", "Deliveries opened no region.");
        Assert.Equal("true", await TextAsync($"document.querySelector(\"{button}\").getAttribute('aria-expanded')"));

        DateTimeOffset until = DateTimeOffset.UtcNow + Patience;
        string pressed = "deliveries";
        while (true)
        {
            string region = await TextAsync($"document.getElementById('whSent-{scope}')?.innerText || ''");
            if (region.Contains("Delivered", StringComparison.Ordinal) && region.Contains("200", StringComparison.Ordinal))
            {
                break;
            }

            Assert.True(DateTimeOffset.UtcNow < until, $"The deliveries never said Delivered with 200; they say: {region}");
            await Task.Delay(1000);
            await ClickAsync($"[data-wh-act=refresh][data-wh-scope={scope}][data-wh-id='{id}']");
            pressed = "refresh";
            await Task.Delay(300);
        }

        Assert.True(await Browser.EvaluateAsync<bool>(
            $"document.activeElement?.dataset?.whAct === '{pressed}'"), $"The redraw after {pressed} dropped the focus.");
    }

    [Fact]
    public async Task An_administrator_creates_deactivates_activates_and_deletes_an_organizations_webhook_and_reads_its_deliveries()
    {
        (string token, _) = await SignInAsync();
        using Receiver receiver = new();
        string name = "zz ui hook " + Guid.NewGuid().ToString("N")[..6];
        string? item = null;

        try
        {
            // First run: the column offers Webhooks, and an organization with none is the empty state, not an empty table.
            bool none = (await OrganizationsWebhooksAsync()).Length == 0;
            await OpenAsync("/studio/general", token);
            await WaitForAsync(Shown(".view.on > nav.subpages a[href$='/webhooks']"), "Settings' column does not offer Webhooks.");
            await ClickAsync(".view.on > nav.subpages a[href$='/webhooks']");
            await WaitForAsync("document.getElementById('view-webhooks')?.classList.contains('on')"
                + " && document.getElementById('whList')?.getAttribute('aria-busy') !== 'true'", "Webhooks never drew.");
            await LetWritesAsync();
            if (none)
            {
                Assert.True(await Browser.EvaluateAsync<bool>(Shown("#whList .whempty")), "No webhooks drew no empty state.");
                Assert.Equal(string.Empty, await TextAsync("document.getElementById('whCount').textContent"));
            }

            Assert.True(await Browser.EvaluateAsync<bool>(Shown("#whNew")), "Create webhook is not shown.");
            Assert.Equal("polite", await TextAsync("document.getElementById('whSays').getAttribute('aria-live')"));

            // Create: refused without events once Selected events is chosen, then made for /items/add.
            await ClickAsync("#whNew");
            await WaitForAsync("document.getElementById('whDialog').open", "Create webhook opened no dialog.");
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#whActiveRow")), "An organization's webhook offers Active, which Portal does not.");
            await TypeAsync("whName", name);
            await TypeAsync("whUrl", receiver.Url);
            await TypeAsync("whSecret", "zz-secret");
            await ClickAsync("#whEvents input[name=whWhich][value=some]");
            await ClickAsync("#whCreate");
            await WaitForAsync(Shown("#whEventsSays"), "Selected events with none ticked was not refused under Events.");
            Assert.Equal("whEventsSays", await TextAsync("document.getElementById('whEvents').getAttribute('aria-describedby') || ''"));
            await ClickAsync("#whEvents input[data-wh-event='/items/add']");
            await ClickAsync("#whCreate");
            await WaitForAsync("!document.getElementById('whDialog').open && !!document.querySelector('#whList tr[data-wh-row]')",
                "The new webhook is not listed.");

            JsonElement made = (await OrganizationsWebhooksAsync()).Single(w => w.GetProperty("name").GetString() == name);
            string id = made.GetProperty("id").GetString()!;
            Assert.Equal(["/items/add"], made.GetProperty("events").EnumerateArray().Select(e => e.GetString()));
            Assert.True(made.GetProperty("hasSecret").GetBoolean());
            Assert.Contains("is created", await TextAsync("document.getElementById('whSays').textContent"), StringComparison.Ordinal);
            Assert.True(await Browser.EvaluateAsync<bool>($"document.activeElement?.dataset?.whId === '{id}'"), "Focus did not land on the new row.");
            string row = await TextAsync($"document.querySelector(\"#whList tr[data-wh-row='{id}']\").innerText");
            Assert.Contains("Items: added", row, StringComparison.Ordinal);
            Assert.Contains("Active", row, StringComparison.Ordinal);
            Assert.Contains(receiver.Url, row, StringComparison.Ordinal);

            // Deactivate, then activate, from the row's ⋯, each read back from the server, the focus back on the ⋯.
            await MenuAsync("org", id, "toggle");
            await WaitForAsync($"/Inactive/.test(document.querySelector(\"#whList tr[data-wh-row='{id}']\")?.innerText || '')", "Deactivate did not draw it inactive.");
            Assert.False((await OrganizationsWebhooksAsync()).Single(w => w.GetProperty("id").GetString() == id).GetProperty("isActive").GetBoolean());
            Assert.True(await Browser.EvaluateAsync<bool>("document.activeElement?.dataset?.whAct === 'menu'"), "Focus was dropped by the redraw.");
            await MenuAsync("org", id, "toggle");
            await WaitForAsync($"/Deactivate/.test(document.querySelector(\"[data-wh-act=toggle][data-wh-id='{id}']\")?.textContent || '')", "Activate did not draw it active.");
            Assert.True((await OrganizationsWebhooksAsync()).Single(w => w.GetProperty("id").GetString() == id).GetProperty("isActive").GetBoolean());

            // Its deliveries: none before an event, and the receiver's 200 after one.
            await ClickAsync($"[data-wh-act=deliveries][data-wh-id='{id}']");
            await WaitForAsync("/No deliveries yet/.test(document.getElementById('whSent-org')?.innerText || '')", "No deliveries drew no sentence.");
            await ClickAsync($"[data-wh-act=deliveries][data-wh-id='{id}']");
            await WaitForAsync("!document.getElementById('whSent-org')", "Deliveries did not close again.");

            JsonElement added = await PostAsync($"/sharing/rest/content/users/{User}/addItem",
                new() { ["type"] = "Web Mapping Application", ["title"] = "zz webhook screen probe", ["text"] = "{}" });
            item = added.GetProperty("id").GetString();
            await receiver.WaitAsync(Patience);
            await DeliveredAsync("org", id);
            NothingWentWrong(await PageErrorsAsync());

            // Delete, confirmed, and gone from the server.
            await MenuAsync("org", id, "delete");
            await ConfirmAsync(name);
            await WaitForAsync($"!document.querySelector(\"#whList tr[data-wh-row='{id}']\")", "The deleted webhook is still listed.");
            Assert.DoesNotContain(await OrganizationsWebhooksAsync(), w => w.GetProperty("id").GetString() == id);
            Assert.Contains("is deleted", await TextAsync("document.getElementById('whSays').textContent"), StringComparison.Ordinal);
            NothingWentWrong(await PageErrorsAsync());

            // At a phone's width the row's buttons are a finger's size and the page does not scroll sideways.
            await Browser.CallAsync("Emulation.setDeviceMetricsOverride", new { width = 390, height = 844, deviceScaleFactor = 2, mobile = true });
            try
            {
                await OpenAsync("/studio/webhooks", token);
                await WaitForAsync(Shown("#whNew"), "Webhooks did not draw at 390 pixels.");
                Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('whNew').getBoundingClientRect().height >= 44"),
                    "Create webhook is smaller than 44 pixels on a phone.");
                Assert.True(await Browser.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth + 1"),
                    "Webhooks scrolls sideways at 390 pixels.");
            }
            finally
            {
                await Browser.CallAsync("Emulation.clearDeviceMetricsOverride");
            }
        }
        finally
        {
            foreach (JsonElement w in await OrganizationsWebhooksAsync())
            {
                if (w.GetProperty("name").GetString() == name)
                {
                    await PostAsync($"{Portal}/{w.GetProperty("id").GetString()}/delete");
                }
            }

            if (item is not null)
            {
                await PostAsync($"/sharing/rest/content/users/{User}/items/{item}/delete", new() { ["permanentDelete"] = "true" });
            }
        }
    }

    [Fact]
    public async Task A_member_who_does_not_manage_security_is_offered_no_webhooks_and_no_control_that_writes_them()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/general", token, null, "admin:manageSecurity");
        await WaitForAsync("document.querySelectorAll('.view.on > nav.subpages a').length > 0", "Settings drew no column.");
        Assert.False(await Browser.EvaluateAsync<bool>("!!document.querySelector(\".view.on > nav.subpages a[href$='/webhooks']\")"),
            "The column offers Webhooks to a reader the API refuses.");

        await OpenAsync("/studio/webhooks", token, null, "admin:manageSecurity");
        await WaitForAsync("document.getElementById('view-webhooks')?.classList.contains('on')"
            + " && document.getElementById('whList')?.getAttribute('aria-busy') !== 'true'", "Webhooks never drew.");
        Assert.False(await Browser.EvaluateAsync<bool>(Shown("#whNew")), "Create webhook is offered to a reader the API refuses.");
        Assert.False(await Browser.EvaluateAsync<bool>("!!document.querySelector('#whList [data-wh-act]')"), "A row action is offered.");
        Assert.Contains("managed by an administrator", await TextAsync("document.getElementById('whList').textContent"), StringComparison.Ordinal);
        Assert.Equal(0, await Browser.EvaluateAsync<int>("(window.__calls || []).filter(c => /webhooks/.test(c.u)).length"));
        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task A_feature_services_settings_list_its_webhooks_with_create_activate_deactivate_delete_and_deliveries()
    {
        string qualified = Environment.GetEnvironmentVariable("GRATICULA_TEST_EDITABLE") ?? "hosted/ci_editable";
        string admin = $"/rest/admin/services/{qualified}/FeatureServer/webhooks";
        (string token, _) = await SignInAsync();
        using Receiver receiver = new();
        string name = "zz ui edits " + Guid.NewGuid().ToString("N")[..6];
        long? feature = null;

        try
        {
            bool none = (await ServicesWebhooksAsync(admin)).Length == 0;
            await OpenAsync($"/studio/#/service/{qualified}?tab=settings&section=webhooks", token);
            await WaitForAsync("!!document.querySelector('#page-webhooks.on')"
                + " && document.getElementById('svcWhList')?.getAttribute('aria-busy') !== 'true'", "Settings › Webhooks never drew.");
            Assert.True(await Browser.EvaluateAsync<bool>(Shown("#serviceNav a[data-service-page=webhooks]")), "Settings does not list Webhooks.");
            await LetWritesAsync();
            if (none)
            {
                Assert.True(await Browser.EvaluateAsync<bool>(Shown("#svcWhList .whempty")), "No webhooks drew no empty state.");
                Assert.Equal(string.Empty, await TextAsync("document.getElementById('svcWhCount').textContent"));
            }

            // Create: FeaturesCreated alone, active.
            await ClickAsync("#svcWhNew");
            await WaitForAsync("document.getElementById('whDialog').open", "Create webhook opened no dialog.");
            Assert.True(await Browser.EvaluateAsync<bool>(Shown("#whActiveRow")), "A service's webhook does not offer Active.");
            await TypeAsync("whName", name);
            await TypeAsync("whUrl", receiver.Url);
            foreach (string other in new[] { "FeaturesUpdated", "FeaturesDeleted" })
            {
                await ClickAsync($"#whEvents input[data-wh-event={other}]");
            }

            await ClickAsync("#whCreate");
            await WaitForAsync("!document.getElementById('whDialog').open && !!document.querySelector('#svcWhList tr[data-wh-row]')",
                "The new webhook is not listed.");

            JsonElement made = (await ServicesWebhooksAsync(admin)).Single(w => w.GetProperty("name").GetString() == name);
            string id = made.GetProperty("id").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(["FeaturesCreated"], made.GetProperty("changeTypes").EnumerateArray().Select(e => e.GetString()));
            Assert.True(made.GetProperty("active").GetBoolean());
            Assert.Contains("Features created", await TextAsync($"document.querySelector(\"#svcWhList tr[data-wh-row='{id}']\").innerText"),
                StringComparison.Ordinal);

            // Deactivate and activate, through the webhook's edit.
            await MenuAsync("svc", id, "toggle");
            await WaitForAsync($"/Inactive/.test(document.querySelector(\"#svcWhList tr[data-wh-row='{id}']\")?.innerText || '')", "Deactivate did not draw it inactive.");
            Assert.False((await ServicesWebhooksAsync(admin)).Single(w => w.GetProperty("name").GetString() == name).GetProperty("active").GetBoolean());
            await MenuAsync("svc", id, "toggle");
            await WaitForAsync($"/Deactivate/.test(document.querySelector(\"[data-wh-act=toggle][data-wh-scope=svc][data-wh-id='{id}']\")?.textContent || '')",
                "Activate did not draw it active.");
            Assert.True((await ServicesWebhooksAsync(admin)).Single(w => w.GetProperty("name").GetString() == name).GetProperty("active").GetBoolean());

            // An edit, and its delivery.
            (int code, string body) = await AdminAsync(HttpMethod.Post,
                $"/rest/services/{qualified}/FeatureServer/0/addFeatures?f=json&features="
                + Uri.EscapeDataString("[{\"attributes\":{},\"geometry\":{\"x\":29.03,\"y\":41.03,\"spatialReference\":{\"wkid\":4326}}}]"));
            Assert.True(code == 200, $"addFeatures answered {code}: {body}");
            feature = JsonDocument.Parse(body).RootElement.GetProperty("addResults")[0].GetProperty("objectId").GetInt64();
            await receiver.WaitAsync(Patience);
            await DeliveredAsync("svc", id);
            NothingWentWrong(await PageErrorsAsync());

            await MenuAsync("svc", id, "delete");
            await ConfirmAsync(name);
            await WaitForAsync($"!document.querySelector(\"#svcWhList tr[data-wh-row='{id}']\")", "The deleted webhook is still listed.");
            Assert.DoesNotContain(await ServicesWebhooksAsync(admin), w => w.GetProperty("name").GetString() == name);
            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            if (feature is { } left)
            {
                await AdminAsync(HttpMethod.Post, $"/rest/services/{qualified}/FeatureServer/0/deleteFeatures?f=json&objectIds={left}");
            }

            foreach (JsonElement w in await ServicesWebhooksAsync(admin))
            {
                if (w.GetProperty("name").GetString() == name)
                {
                    await PostAsync($"{admin}/{w.GetProperty("id").GetInt32()}/delete");
                }
            }
        }
    }

    /// <summary>
    /// The UX review of 2026-11-04, M1, M2 and m2: each refusal is under its own field and in sight, on a phone where the
    /// dialog scrolls; a bare address is refused before anything is posted; a server sentence that names a parameter is
    /// said in the field's words; and the event boxes are there only under Selected events.
    /// </summary>
    [Fact]
    public async Task The_dialog_says_each_refusal_under_its_field_in_sight_and_checks_the_address_before_posting()
    {
        (string token, _) = await SignInAsync();
        string name = "zz ui refusals " + Guid.NewGuid().ToString("N")[..6];
        const string NoCreate = "(window.__writes || []).filter(w => /createWebhook/.test(w)).length === 0";

        await Browser.CallAsync("Emulation.setDeviceMetricsOverride", new { width = 390, height = 844, deviceScaleFactor = 2, mobile = true });
        try
        {
            await OpenAsync("/studio/webhooks", token);
            await WaitForAsync(Shown("#whNew") + " && document.getElementById('whList')?.getAttribute('aria-busy') !== 'true'", "Webhooks never drew.");
            await LetWritesAsync();
            await ClickAsync("#whNew");
            await WaitForAsync("document.getElementById('whDialog').open", "Create webhook opened no dialog.");

            // All events, the default, shows no boxes; Selected events does.
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#whPicks input[data-wh-event]")), "The event boxes show under All events.");
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('whPicks').inert"), "The hidden event boxes are not inert.");
            await ClickAsync("#whEvents input[name=whWhich][value=some]");
            await WaitForAsync(Shown("#whPicks input[data-wh-event]"), "Selected events showed no boxes.");

            // Nothing filled in: each field says why under itself, the first is focused and in sight, and nothing is sent.
            await ClickAsync("#whCreate");
            await WaitForAsync(Shown("#whNameSays"), "An empty name was not refused under Name.");
            Assert.True(await Browser.EvaluateAsync<bool>(Shown("#whUrlSays")), "An empty URL was not refused under Payload URL.");
            Assert.True(await Browser.EvaluateAsync<bool>(Shown("#whEventsSays")), "No event was not refused under Events.");
            Assert.True(await Browser.EvaluateAsync<bool>("document.activeElement?.id === 'whName'"), "The first refused field is not focused.");
            Assert.Equal("true", await TextAsync("document.getElementById('whName').getAttribute('aria-invalid')"));
            Assert.Contains("whNameSays", await TextAsync("document.getElementById('whName').getAttribute('aria-describedby')"), StringComparison.Ordinal);
            Assert.True(await Browser.EvaluateAsync<bool>(InView("whNameSays")), "Name's refusal is not in the window.");
            Assert.Equal("whEventsSays", await TextAsync("document.getElementById('whEvents').getAttribute('aria-describedby') || ''"));
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.querySelector('#whEvents [aria-invalid]')"), "A single event box is marked invalid.");
            Assert.True(await Browser.EvaluateAsync<bool>(NoCreate), "An empty form was posted.");

            // Ticking an event clears the group's refusal.
            await ClickAsync("#whEvents input[data-wh-event='/items/add']");
            await WaitForAsync("!document.getElementById('whEvents').hasAttribute('aria-describedby')", "Ticking an event left its refusal.");

            // A bare address, with the reader at the bottom of the dialog: refused here, under the URL, brought into sight.
            await TypeAsync("whName", name);
            await TypeAsync("whUrl", "hooks.example.com/graticula");
            await Browser.EvaluateAsync<bool>("(() => { document.querySelector('#whDialog .dialogbody').scrollTop = 1e6; return true; })()");
            await ClickAsync("#whCreate");
            await WaitForAsync("/starting with https:\\/\\//.test(document.getElementById('whUrlSays').textContent)",
                "An address without its scheme was not refused under Payload URL.");
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#whNameSays")), "Name's refusal stayed after a name was given.");
            Assert.True(await Browser.EvaluateAsync<bool>("document.activeElement?.id === 'whUrl'"), "Payload URL is not focused.");
            Assert.True(await Browser.EvaluateAsync<bool>(InView("whUrlSays")), "The URL's refusal is not in the window.");
            Assert.True(await Browser.EvaluateAsync<bool>(NoCreate), "An address without its scheme was posted.");

            // The server's refusal of a URL names its parameter; the dialog says it in the field's words.
            await TypeAsync("whUrl", "https://hooks.example.com/" + new string('a', 2100));
            await ClickAsync("#whCreate");
            await WaitForAsync("/2,048 characters/.test(document.getElementById('whUrlSays').textContent)",
                "The server's refusal of a long URL was not said under Payload URL.");
            Assert.DoesNotContain("'url'", await TextAsync("document.getElementById('whDialog').innerText"), StringComparison.Ordinal);
            Assert.True(await Browser.EvaluateAsync<bool>(InView("whUrlSays")), "The server's refusal is not in the window.");

            await ClickAsync("#whCancel");
            await WaitForAsync("!document.getElementById('whDialog').open && document.activeElement?.id === 'whNew'",
                "Cancel did not hand the focus back to Create webhook.");
            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await Browser.CallAsync("Emulation.clearDeviceMetricsOverride");
            foreach (JsonElement w in await OrganizationsWebhooksAsync())
            {
                if (w.GetProperty("name").GetString() == name)
                {
                    await PostAsync($"{Portal}/{w.GetProperty("id").GetString()}/delete");
                }
            }
        }
    }

    /// <summary>
    /// M3 and m1: an organization's webhook is edited from its row's ⋯ — the URL changed, the secret left empty and so
    /// kept — and the ⋯ closes on Escape with the focus on it.
    /// </summary>
    [Fact]
    public async Task An_organizations_webhook_is_edited_from_its_row_menu_and_keeps_its_secret()
    {
        (string token, _) = await SignInAsync();
        using Receiver first = new();
        using Receiver second = new();
        string name = "zz ui edit " + Guid.NewGuid().ToString("N")[..6];

        try
        {
            JsonElement made = await PostAsync($"{Portal}/createWebhook", new()
            {
                ["name"] = name, ["url"] = first.Url, ["secret"] = "zz-secret", ["changes"] = "manualChanges", ["events"] = "/items/add",
            });
            string id = made.GetProperty("webhook").GetProperty("id").GetString()!;

            await OpenAsync("/studio/webhooks", token);
            await WaitForAsync($"!!document.querySelector(\"#whList tr[data-wh-row='{id}']\")", "The webhook is not listed.");
            await LetWritesAsync();
            Assert.Contains("<wbr>", await TextAsync($"document.querySelector(\"#whList tr[data-wh-row='{id}'] code.whurl\").innerHTML"),
                StringComparison.Ordinal);

            // The ⋯: Escape closes it and hands the focus back to it.
            string summary = $"[data-wh-act=menu][data-wh-scope=org][data-wh-id='{id}']";
            await ClickAsync(summary);
            await WaitForAsync(Shown($"[data-wh-act=edit][data-wh-scope=org][data-wh-id='{id}']"), "The ⋯ did not open.");
            await Browser.EvaluateAsync<bool>($"(() => {{ document.querySelector(\"[data-wh-act=edit][data-wh-id='{id}']\").focus(); return true; }})()");
            await Browser.CallAsync("Input.dispatchKeyEvent", new { type = "keyDown", key = "Escape", code = "Escape", windowsVirtualKeyCode = 27 });
            await Browser.CallAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "Escape", code = "Escape", windowsVirtualKeyCode = 27 });
            await WaitForAsync($"!document.querySelector(\"{summary}\").parentElement.open", "Escape did not close the ⋯.");
            Assert.True(await Browser.EvaluateAsync<bool>($"document.activeElement === document.querySelector(\"{summary}\")"),
                "Escape did not hand the focus back to the ⋯.");

            // Edit: filled in, the secret empty with a word that it is kept.
            await MenuAsync("org", id, "edit");
            await WaitForAsync("document.getElementById('whDialog').open", "Edit opened no dialog.");
            Assert.StartsWith("Edit webhook", await TextAsync("document.getElementById('whDialogTitle').textContent"), StringComparison.Ordinal);
            Assert.Equal("Save", await TextAsync("document.getElementById('whCreate').textContent"));
            Assert.Equal(name, await TextAsync("document.getElementById('whName').value"));
            Assert.Equal(first.Url, await TextAsync("document.getElementById('whUrl').value"));
            Assert.Equal(string.Empty, await TextAsync("document.getElementById('whSecret').value"));
            Assert.True(await Browser.EvaluateAsync<bool>(Shown("#whSecretKeep")), "Edit does not say an empty secret keeps the current one.");
            Assert.True(await Browser.EvaluateAsync<bool>("document.querySelector('#whEvents input[name=whWhich][value=some]').checked"
                + " && document.querySelector(\"#whEvents input[data-wh-event='/items/add']\").checked"), "Edit did not tick the webhook's events.");

            await TypeAsync("whUrl", second.Url);
            await ClickAsync("#whCreate");
            await WaitForAsync("!document.getElementById('whDialog').open", "Save did not close the dialog.");
            await WaitForAsync($"document.activeElement === document.querySelector(\"{summary}\")", "Save did not hand the focus back to the row's ⋯.");

            JsonElement read = (await OrganizationsWebhooksAsync()).Single(w => w.GetProperty("id").GetString() == id);
            Assert.Equal(second.Url, read.GetProperty("payloadUrl").GetString());
            Assert.True(read.GetProperty("hasSecret").GetBoolean(), "Save with the secret left empty removed the secret.");
            Assert.Equal(["/items/add"], read.GetProperty("events").EnumerateArray().Select(e => e.GetString()));
            await WaitForAsync("/is saved/.test(document.getElementById('whSays').textContent)", "Save said nothing.");
            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            foreach (JsonElement w in await OrganizationsWebhooksAsync())
            {
                if (w.GetProperty("name").GetString() == name)
                {
                    await PostAsync($"{Portal}/{w.GetProperty("id").GetString()}/delete");
                }
            }
        }
    }

    /// <summary>M3 on a feature service: its webhook's URL changed through <c>edit</c>, its signature key kept, still active.</summary>
    [Fact]
    public async Task A_feature_services_webhook_is_edited_and_keeps_its_signature_key()
    {
        string qualified = Environment.GetEnvironmentVariable("GRATICULA_TEST_EDITABLE") ?? "hosted/ci_editable";
        string admin = $"/rest/admin/services/{qualified}/FeatureServer/webhooks";
        (string token, _) = await SignInAsync();
        using Receiver first = new();
        using Receiver second = new();
        string name = "zz ui svc edit " + Guid.NewGuid().ToString("N")[..6];

        try
        {
            JsonElement made = await PostAsync($"{admin}/create", new()
            {
                ["name"] = name, ["hookUrl"] = first.Url, ["changeTypes"] = "[\"FeaturesCreated\"]", ["signatureKey"] = "zz-key", ["active"] = "true",
            });
            string id = made.GetProperty("id").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);

            await OpenAsync($"/studio/#/service/{qualified}?tab=settings&section=webhooks", token);
            await WaitForAsync($"!!document.querySelector(\"#svcWhList tr[data-wh-row='{id}']\")", "The webhook is not listed.");
            await LetWritesAsync();

            await MenuAsync("svc", id, "edit");
            await WaitForAsync("document.getElementById('whDialog').open", "Edit opened no dialog.");
            Assert.Equal(first.Url, await TextAsync("document.getElementById('whUrl').value"));
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('whActive').checked"), "Edit drew an active webhook inactive.");
            Assert.True(await Browser.EvaluateAsync<bool>("document.querySelector('#whEvents input[data-wh-event=FeaturesCreated]').checked"
                + " && !document.querySelector('#whEvents input[data-wh-event=FeaturesUpdated]').checked"), "Edit did not tick the webhook's change types.");
            Assert.True(await Browser.EvaluateAsync<bool>(Shown("#whSecretKeep")), "Edit does not say an empty secret keeps the current one.");

            await TypeAsync("whUrl", second.Url);
            await ClickAsync("#whCreate");
            await WaitForAsync("!document.getElementById('whDialog').open", "Save did not close the dialog.");
            await WaitForAsync($"document.activeElement === document.querySelector(\"[data-wh-act=menu][data-wh-scope=svc][data-wh-id='{id}']\")",
                "Save did not hand the focus back to the row's ⋯.");

            JsonElement read = (await ServicesWebhooksAsync(admin)).Single(w => w.GetProperty("name").GetString() == name);
            Assert.Equal(second.Url, read.GetProperty("hookUrl").GetString());
            Assert.True(read.GetProperty("hasSignatureKey").GetBoolean(), "Save with the secret left empty removed the signature key.");
            Assert.True(read.GetProperty("active").GetBoolean());
            Assert.Equal(["FeaturesCreated"], read.GetProperty("changeTypes").EnumerateArray().Select(e => e.GetString()));
            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            foreach (JsonElement w in await ServicesWebhooksAsync(admin))
            {
                if (w.GetProperty("name").GetString() == name)
                {
                    await PostAsync($"{admin}/{w.GetProperty("id").GetInt32()}/delete");
                }
            }
        }
    }

    /// <summary>
    /// M4: a delivery the receiver refused, which the server will try again, is amber and says <i>Failed, retrying</i>;
    /// and a webhook the server switched off says so, with its reason.
    /// </summary>
    [Fact]
    public async Task A_refused_delivery_reads_as_a_failure_and_a_webhook_the_server_switched_off_says_so()
    {
        (string token, _) = await SignInAsync();
        using Receiver refusing = new("500 Internal Server Error");
        string name = "zz ui failing " + Guid.NewGuid().ToString("N")[..6];
        string? item = null;

        try
        {
            JsonElement made = await PostAsync($"{Portal}/createWebhook", new()
            {
                ["name"] = name, ["url"] = refusing.Url, ["changes"] = "manualChanges", ["events"] = "/items/add",
            });
            string id = made.GetProperty("webhook").GetProperty("id").GetString()!;
            JsonElement added = await PostAsync($"/sharing/rest/content/users/{User}/addItem",
                new() { ["type"] = "Web Mapping Application", ["title"] = "zz webhook failure probe", ["text"] = "{}" });
            item = added.GetProperty("id").GetString();
            await refusing.WaitAsync(Patience);

            await OpenAsync("/studio/webhooks", token);
            await WaitForAsync($"!!document.querySelector(\"#whList tr[data-wh-row='{id}']\")", "The webhook is not listed.");
            await ClickAsync($"[data-wh-act=deliveries][data-wh-scope=org][data-wh-id='{id}']");
            await WaitForAsync("!!document.getElementById('whSent-org')", "Deliveries opened no region.");

            DateTimeOffset until = DateTimeOffset.UtcNow + Patience;
            while (!(await TextAsync("document.getElementById('whSent-org')?.innerText || ''")).Contains("Failed, retrying", StringComparison.Ordinal))
            {
                Assert.True(DateTimeOffset.UtcNow < until,
                    $"A refused delivery never said Failed, retrying; it says: {await TextAsync("document.getElementById('whSent-org')?.innerText || ''")}");
                await Task.Delay(1000);
                await ClickAsync($"[data-wh-act=refresh][data-wh-scope=org][data-wh-id='{id}']");
                await Task.Delay(300);
            }

            Assert.True(await Browser.EvaluateAsync<bool>(
                "[...document.querySelectorAll('#whSent-org .pill')].some(p => p.textContent === 'Failed, retrying' && p.classList.contains('p-stopped'))"),
                "Failed, retrying is not the amber pill.");
            Assert.Contains("Items: added", await TextAsync("document.getElementById('whSent-org').innerText"), StringComparison.Ordinal);

            // The list says it too, without Deliveries being opened: a webhook still Active whose every message bounces
            // looked healthy in its row.
            await OpenAsync("/studio/webhooks", token);
            await WaitForAsync(
                $"(document.querySelector(\"#whList tr[data-wh-row='{id}'] .whlastretry\")?.textContent || '').startsWith('Last delivery failed, retrying')",
                "The row does not say its last delivery failed and is being tried again.");
            Assert.Contains("receiver answered 500", await TextAsync($"document.querySelector(\"#whList tr[data-wh-row='{id}'] .whlastretry\").textContent"),
                StringComparison.Ordinal);
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.getElementById('whSent-org')"),
                "The note was read from an opened Deliveries region rather than the list.");

            // The server switches a webhook off after its failures and gives the reason; reaching that takes days of
            // failures, so the row is drawn from such a webhook's document as the list reads it.
            Assert.True(await Browser.EvaluateAsync<bool>($$"""
                (() => {
                  const w = webhookLists.org.hooks.find(x => x.id === {{JsonSerializer.Serialize(id)}});
                  w.active = false; w.reason = "Deactivated after 5 failed deliveries in 5 days.";
                  webhookDraw("org");
                  return true;
                })()
                """));
            string row = await TextAsync($"document.querySelector(\"#whList tr[data-wh-row='{id}']\").innerText");
            Assert.Contains("Deactivated by server", row, StringComparison.Ordinal);
            Assert.Contains("5 failed deliveries", row, StringComparison.Ordinal);
            Assert.True(await Browser.EvaluateAsync<bool>(
                $"document.querySelector(\"#whList tr[data-wh-row='{id}'] .pill\").classList.contains('p-stopped')"), "Deactivated by server is not amber.");
            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            foreach (JsonElement w in await OrganizationsWebhooksAsync())
            {
                if (w.GetProperty("name").GetString() == name)
                {
                    await PostAsync($"{Portal}/{w.GetProperty("id").GetString()}/delete");
                }
            }

            if (item is not null)
            {
                await PostAsync($"/sharing/rest/content/users/{User}/items/{item}/delete", new() { ["permanentDelete"] = "true" });
            }
        }
    }

    /// <summary>The smallest receiver that will do: it reads a request, answers 200 (or the status it is given) and counts it.</summary>
    private sealed class Receiver : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly string _status;
        private int _received;

        public Receiver(string status = "200 OK")
        {
            _status = status;
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/hook";

        public async Task WaitAsync(TimeSpan patience)
        {
            DateTimeOffset until = DateTimeOffset.UtcNow + patience;
            while (Volatile.Read(ref _received) == 0)
            {
                Assert.True(DateTimeOffset.UtcNow < until, $"No delivery reached the receiver within {patience}.");
                await Task.Delay(200);
            }
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    List<byte> head = [];
                    byte[] one = new byte[1];
                    while (head.Count < 65536)
                    {
                        if (await stream.ReadAsync(one) == 0)
                        {
                            return;
                        }

                        head.Add(one[0]);
                        if (head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n')
                        {
                            break;
                        }
                    }

                    string text = Encoding.ASCII.GetString([.. head]);
                    int length = text.Split("\r\n")
                        .Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        .Select(l => int.Parse(l["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture))
                        .FirstOrDefault();
                    byte[] body = new byte[length];
                    int read = 0;
                    while (read < length)
                    {
                        int n = await stream.ReadAsync(body.AsMemory(read));
                        if (n == 0)
                        {
                            break;
                        }

                        read += n;
                    }

                    Interlocked.Increment(ref _received);
                    byte[] answer = Encoding.ASCII.GetBytes($"HTTP/1.1 {_status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(answer);
                }
                catch (Exception e) when (e is System.IO.IOException or SocketException or ObjectDisposedException)
                {
                    // A receiver that drops a connection is the sender's problem, which it reports as a failed delivery.
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
