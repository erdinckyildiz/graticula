using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// ADR-220 in the console: the share dialog offers <em>All organizations on this server</em> where the server holds
/// several organizations and the reader may share at it, saves it, and badges it; and Studio's search has ArcGIS Online's
/// <em>My organization / All organizations</em> switch, whose other-organization hits open at their own address.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asserted as the readers who need it and the ones who must not see it</b>: a member of the default organization
/// whose custom role holds <c>sharing:shareToAllOrganizations</c>, a plain <c>user</c> without it, and the same member
/// at the other organization's address, where they are an outsider. And the first-run state twice — a server of one
/// organization — because that is where most deployments are and where the level means nothing.
/// </para>
/// <para>
/// <b>Every organization made here is purged in <c>finally</c></b>, for the reason <see cref="OrganizationReadersTests"/>
/// gives: while a second one exists the whole server answers as several.
/// </para>
/// </remarks>
public sealed class SharedWithAllOrganizationsTests : ConsoleTest
{
    private const string Privilege = "sharing:shareToAllOrganizations";

    private async Task<(int Status, string Body)> AtHostAsync(
        string host, string? token, HttpMethod method, string path, object? body = null)
    {
        Uri root = new(Root);
        using HttpRequestMessage request = new(method, new Uri($"{Root}{path}"));
        request.Headers.Host = $"{host}:{root.Port}";

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await Http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Signs in with an issued password, replaces it, and signs in again: a usable token.</summary>
    private async Task<string> SettledTokenAsync(string host, string name, string issued)
    {
        (int s, string b) = await AtHostAsync(host, null, HttpMethod.Post, "/rest/auth/login", new { name, password = issued });
        Assert.True(s == 200, $"Signing in as {name} at {host} answered {s}.");
        string first = JsonDocument.Parse(b).RootElement.GetProperty("token").GetString()!;

        string chosen = "Zz" + Guid.NewGuid().ToString("N")[..20] + "!7";
        (s, _) = await AtHostAsync(host, first, HttpMethod.Post, "/rest/auth/password",
            new { currentPassword = issued, newPassword = chosen });
        Assert.True(s is >= 200 and < 300, $"Replacing {name}'s issued password answered {s}.");

        (s, b) = await AtHostAsync(host, null, HttpMethod.Post, "/rest/auth/login", new { name, password = chosen });
        Assert.True(s == 200, $"Signing in again as {name} at {host} answered {s}.");
        return JsonDocument.Parse(b).RootElement.GetProperty("token").GetString()!;
    }

    private async Task<string> MemberAsync(string name, string role)
    {
        (int made, string answer) = await AdminAsync(HttpMethod.Post, "/admin/members",
            JsonSerializer.Serialize(new { name, role }));
        Assert.True(made == 201, $"Making {name} answered {made}.");
        string issued = JsonDocument.Parse(answer).RootElement.GetProperty("password").GetString()!;
        return await SettledTokenAsync(new Uri(Root).Host, name, issued);
    }

    private async Task<string[]> TenantKeysAsync()
    {
        (int status, string body) = await AdminAsync(HttpMethod.Get, "/admin/tenants");
        Assert.True(status == 200, $"GET /admin/tenants answered {status}.");
        return [.. JsonDocument.Parse(body).RootElement.GetProperty("tenants").EnumerateArray().Select(t => t.GetProperty("key").GetString()!)];
    }

    /// <summary>Makes a second organization with one host and returns its administrator's token, or null where the store cannot.</summary>
    private async Task<string?> SecondOrganizationAsync(string key, string name, string host)
    {
        (int made, string answer) = await AdminAsync(HttpMethod.Post, "/admin/tenants",
            JsonSerializer.Serialize(new { key, name, hosts = new[] { host }, administrator = $"{key}_admin" }));

        if (made == 409 && answer.Contains("superuser", StringComparison.Ordinal))
        {
            return null;
        }

        Assert.True(made == 201, $"Making {key} answered {made}.");
        string issued = JsonDocument.Parse(answer).RootElement.GetProperty("administrator").GetProperty("password").GetString()!;
        return await SettledTokenAsync(host, $"{key}_admin", issued);
    }

    private async Task PurgeAsync(string key)
    {
        if ((await TenantKeysAsync()).Contains(key))
        {
            (int purged, _) = await AdminAsync(HttpMethod.Delete, $"/admin/tenants/{key}?purge=true");
            Assert.True(purged == 200, $"Purging {key} answered {purged}. The server still holds a second organization.");
        }
    }

    /// <summary>Opens Studio › Content at home and the Share dialog of one service, and returns the levels it offers.</summary>
    private async Task<string[]> OfferedLevelsAsync(string token, string qualified)
    {
        string selector = $"[data-share=\"{qualified}\"]";
        await OpenAsync("/studio/#/content", token);
        await WaitForAsync($"!!document.querySelector({JsonSerializer.Serialize(selector)})", $"{qualified} is not in the content list.");
        await ClickAsync(selector);
        await WaitForAsync("!!document.querySelector('input[name=shareScope]')", "The Share dialog drew no level.");
        return await Browser.EvaluateAsync<string[]>(
            "[...document.querySelectorAll('input[name=shareScope]')].map(i => i.value)") ?? [];
    }

    [Fact]
    public async Task The_share_dialog_offers_every_organization_only_where_there_are_several_and_the_reader_may()
    {
        Assert.Equal(["default"], await TenantKeysAsync());
        string run = Guid.NewGuid().ToString("N")[..8];
        string key = "zza" + run;
        string role = "zz_sharer_" + run;
        string sharer = "zz_sharer_" + run;
        string plain = "zz_plain_" + run;
        string layer = "zz_allorgs_" + run;
        string plainLayer = "zz_plainorgs_" + run;
        string adminLayer = "zz_adminorgs_" + run;

        try
        {
            (int s, _) = await AdminAsync(HttpMethod.Post, "/admin/roles", JsonSerializer.Serialize(new
            {
                name = role, description = "Shares with every organization",
                privileges = new[] { "content:create", "content:publishFeatures", "sharing:shareToOrganization", Privilege, "sharing:shareToPublic" },
            }));
            Assert.True(s == 201, $"Making the role {role} answered {s}.");
            string sharerToken = await MemberAsync(sharer, role);
            // A publisher: may publish and share publicly, and holds no privilege to share with every organization.
            string plainToken = await MemberAsync(plain, "publisher");

            string host = new Uri(Root).Host;
            foreach ((string token, string name) in new[] { (sharerToken, layer), (plainToken, plainLayer) })
            {
                (s, _) = await AtHostAsync(host, token, HttpMethod.Post, "/admin/hosted/define",
                    new { name, geometryType = "Point", srid = 4326, fields = Array.Empty<object>() });
                Assert.True(s == 201, $"Defining {name} answered {s}.");
            }

            // <b>A server of one organization offers nothing new</b>, to the member who holds the privilege.
            Assert.Equal(["private", "organization", "public"], await OfferedLevelsAsync(sharerToken, $"hosted/{layer}"));
            NothingWentWrong(await PageErrorsAsync());

            if (await SecondOrganizationAsync(key, "Zz Partners", $"{key}.localhost") is null)
            {
                return;
            }

            // <b>Several organizations and the privilege: between Organization and Everyone</b>, said in words.
            Assert.Equal(["private", "organization", "allorganizations", "public"],
                await OfferedLevelsAsync(sharerToken, $"hosted/{layer}"));
            string said = await Browser.EvaluateAsync<string>(
                "document.querySelector('[data-scope=allorganizations]').innerText") ?? string.Empty;
            Assert.Contains("All organizations on this server", said, StringComparison.Ordinal);
            Assert.Contains("not public", said, StringComparison.Ordinal);
            Assert.True(await Browser.EvaluateAsync<bool>(Shown("input[name=shareScope][value=allorganizations]")),
                "The level is in the dialog but not visible.");

            // The Organization level says it means this organization's members, not everybody on the server.
            string organization = await Browser.EvaluateAsync<string>(
                "document.querySelector('[data-scope=organization]').innerText") ?? string.Empty;
            Assert.Contains("this organization", organization, StringComparison.Ordinal);
            Assert.DoesNotContain("this server", organization, StringComparison.Ordinal);

            // <b>Saved for real</b>: the one write this test lets through is the level's own.
            await Browser.EvaluateAsync<bool>("(window.__letWritesTo = ['/sharing?folder='], true)");
            await ClickAsync("input[name=shareScope][value=allorganizations]");
            await ClickAsync("#shareSave");
            await WaitForAsync("!document.getElementById('share').open", "Save did not close the Share dialog.");

            (s, string items) = await AtHostAsync(host, sharerToken, HttpMethod.Get, "/content/items");
            Assert.Equal(200, s);
            string stored = JsonDocument.Parse(items).RootElement.GetProperty("items").EnumerateArray()
                .First(i => i.GetProperty("name").GetString() == $"hosted/{layer}").GetProperty("sharing").GetString()!;
            Assert.Equal("allorganizations", stored);

            // <b>Its badge</b>: its own words and glyph, never the stored value.
            string pill = $"[data-share=\"hosted/{layer}\"] .pill";
            await WaitForAsync($"(document.querySelector({JsonSerializer.Serialize(pill)})?.textContent || '').trim() === 'All organizations'",
                "The content row does not badge the item as shared with all organizations.");
            Assert.True(await Browser.EvaluateAsync<bool>(
                $"document.querySelector({JsonSerializer.Serialize(pill)}).classList.contains('p-allorganizations') && !!document.querySelector({JsonSerializer.Serialize(pill)} + ' svg')"),
                "The badge has no class or glyph of its own.");
            Assert.DoesNotContain("allorganizations",
                await Browser.EvaluateAsync<string>("document.getElementById('contentRows').innerText") ?? string.Empty, StringComparison.Ordinal);

            // Listed where this organization's organization-wide items are.
            await OpenAsync("/studio/#/content/organization", sharerToken);
            await WaitForAsync($"!!document.querySelector({JsonSerializer.Serialize($"[data-share=\"hosted/{layer}\"]")})",
                "An item shared with every organization is not under Organization.");
            NothingWentWrong(await PageErrorsAsync());

            // <b>The administrator holds it by default</b> (ADR-220 §5.2): offered on their own layer, and saved for real.
            (s, _) = await AdminAsync(HttpMethod.Post, "/admin/hosted/define", JsonSerializer.Serialize(
                new { name = adminLayer, geometryType = "Point", srid = 4326, fields = Array.Empty<object>() }));
            Assert.True(s == 201, $"Defining {adminLayer} answered {s}.");
            (string adminToken, _) = await SignInAsync();
            Assert.Equal(["private", "organization", "allorganizations", "public"],
                await OfferedLevelsAsync(adminToken, $"hosted/{adminLayer}"));

            // The create forms and the bulk Share read the same rule: offered, and choosable.
            Assert.Contains("value=\"allorganizations\">", await Browser.EvaluateAsync<string>("scopeOptions()") ?? string.Empty,
                StringComparison.Ordinal);
            await Browser.EvaluateAsync<bool>("(window.__letWritesTo = ['/sharing?folder='], true)");
            await ClickAsync("input[name=shareScope][value=allorganizations]");
            await ClickAsync("#shareSave");
            await WaitForAsync("!document.getElementById('share').open", "Save did not close the administrator's Share dialog.");
            (s, items) = await AtHostAsync(host, adminToken, HttpMethod.Get, "/content/items");
            Assert.Equal(200, s);
            Assert.Equal("allorganizations", JsonDocument.Parse(items).RootElement.GetProperty("items").EnumerateArray()
                .First(i => i.GetProperty("name").GetString() == $"hosted/{adminLayer}").GetProperty("sharing").GetString());
            NothingWentWrong(await PageErrorsAsync());

            // <b>Without the privilege it is not offered</b>, on the same server of two organizations.
            Assert.Equal(["private", "organization", "public"], await OfferedLevelsAsync(plainToken, $"hosted/{plainLayer}"));
            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await PurgeAsync(key);
            await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{adminLayer}?folder=hosted&drop=true");

            // Their layers go with them, recycle bin included.
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{sharer}?deleteOwned=true");
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{plain}?deleteOwned=true");
            await AdminAsync(HttpMethod.Delete, $"/admin/roles/{role}");
        }
    }

    [Fact]
    public async Task Search_reaches_other_organizations_on_a_switch_and_opens_their_hits_where_they_live()
    {
        Assert.Equal(["default"], await TenantKeysAsync());
        string run = Guid.NewGuid().ToString("N")[..8];
        string key = "zzs" + run;
        string host = $"{key}.localhost";
        string member = "zz_seeker_" + run;
        string layer = "zz_every_" + run;
        const string Organization = "Zz Searchable Partners";

        try
        {
            string token = await MemberAsync(member, "user");

            // <b>A server of one organization has no switch</b>: there is nowhere else to search.
            await OpenAsync($"/studio/#/search?q={run}", token);
            await WaitForAsync("!!document.querySelector('#searchRows td')", "The search page did not draw.");
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#searchWhere")),
                "A server of one organization offers to search all organizations.");

            string? theirs = await SecondOrganizationAsync(key, Organization, host);
            if (theirs is null)
            {
                return;
            }

            (int s, _) = await AtHostAsync(host, theirs, HttpMethod.Post, "/admin/hosted/define",
                new { name = layer, geometryType = "Point", srid = 4326, fields = Array.Empty<object>() });
            Assert.True(s == 201, $"Defining {layer} at {host} answered {s}.");
            (s, _) = await AtHostAsync(host, theirs, HttpMethod.Put, $"/admin/services/{layer}/sharing?folder=hosted",
                new { sharing = "allorganizations" });
            Assert.True(s == 200, $"Sharing {layer} with every organization answered {s}.");

            // <b>At home: My organization first, and nothing of theirs until the switch is pressed.</b>
            await OpenAsync($"/studio/#/search?q={run}", token);
            await WaitForAsync(Shown("#searchWhere"), "The search page offers no switch on a server of several organizations.");
            Assert.Equal("true", await Browser.EvaluateAsync<string>("document.querySelector('[data-search-in=mine]').getAttribute('aria-pressed')"));
            Assert.Equal(0, await Browser.EvaluateAsync<int>("document.querySelectorAll('[data-elsewhere-item]').length"));

            await Browser.EvaluateAsync<bool>("(document.querySelector('[data-search-in=all]').focus(), true)");
            await ClickAsync("[data-search-in=all]");
            await WaitForAsync("!!document.querySelector('[data-elsewhere-item]')",
                "All organizations does not list an item another organization shares with every organization.");
            Assert.Equal("true", await Browser.EvaluateAsync<string>("document.querySelector('[data-search-in=all]').getAttribute('aria-pressed')"));
            Assert.Equal("all", await Browser.EvaluateAsync<string>("document.activeElement?.dataset?.searchIn || ''"));

            string row = await Browser.EvaluateAsync<string>("document.querySelector('[data-elsewhere-item]').innerText") ?? string.Empty;
            Assert.Contains(layer, row, StringComparison.Ordinal);
            Assert.Contains(Organization, row, StringComparison.Ordinal);
            Assert.Contains("All organizations", row, StringComparison.Ordinal);
            Assert.DoesNotContain("allorganizations", row, StringComparison.Ordinal);
            Assert.Contains("in other organizations", await Browser.EvaluateAsync<string>("document.getElementById('searchCount').textContent") ?? string.Empty,
                StringComparison.Ordinal);

            // <b>The link: the item's page at that organization's address, in a new tab that says so.</b>
            Uri root = new(Root);
            string href = await Browser.EvaluateAsync<string>("document.querySelector('[data-elsewhere-item] a')?.href || ''") ?? string.Empty;
            Assert.StartsWith($"{root.Scheme}://{host}:{root.Port}/studio/item/", href, StringComparison.Ordinal);
            Assert.Equal("_blank", await Browser.EvaluateAsync<string>("document.querySelector('[data-elsewhere-item] a').getAttribute('target')"));
            Assert.Contains("noopener", await Browser.EvaluateAsync<string>("document.querySelector('[data-elsewhere-item] a').getAttribute('rel')") ?? string.Empty,
                StringComparison.Ordinal);
            Assert.Contains("opens in a new tab", await Browser.EvaluateAsync<string>("document.querySelector('[data-elsewhere-item] a').textContent") ?? string.Empty,
                StringComparison.Ordinal);

            // The address keeps where it looked: a reload asks the same question in the same place.
            Assert.Contains("in=all", await Browser.EvaluateAsync<string>("location.href") ?? string.Empty, StringComparison.Ordinal);
            NothingWentWrong(await PageErrorsAsync());

            // <b>Followed, as the reader</b>: their session is theirs at every host (ADR-220 §5.6), and the item's page draws
            // there with its level said in words.
            await OpenOnHostAsync(host, new Uri(href).PathAndQuery, token);
            await WaitForAsync($"(document.getElementById('view-service')?.innerText || '').includes({JsonSerializer.Serialize(layer)})",
                "The link does not open the item's page at the other organization's address.");
            await WaitForAsync("[...document.querySelectorAll('#view-service .pill')].some(p => p.getClientRects().length && p.textContent.trim() === 'All organizations')",
                "The item's page, read by an outsider, does not say it is shared with all organizations.");
            NothingWentWrong(await PageErrorsAsync());

            // <b>And there, as an outsider, no switch</b>: they search from home.
            await OpenOnHostAsync(host, $"/studio/#/search?q={run}", token);
            await WaitForAsync("!!document.querySelector('#searchRows td')", "The search page did not draw at the other organization's address.");
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#searchWhere")),
                "A member reading another organization as an outsider is offered to search all organizations from there.");
            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await PurgeAsync(key);
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{member}");
        }
    }

    /// <summary>What the page asked the server and was refused, and what it said in red, since the last call.</summary>
    private async Task<string[]> RefusalsAsync() =>
        await Browser.EvaluateAsync<string[]>("""
            (() => {
              const out = (window.__calls || []).filter(c => c.s === 401 || c.s === 403).map(c => c.s + " " + c.u)
                .concat((window.__toasts || []).map(t => "red: " + t));
              window.__calls = []; window.__toasts = [];
              return out;
            })()
            """) ?? [];

    /// <summary>Opens each tab the item page offers, and returns every refusal met on the way.</summary>
    private async Task<string[]> WalkTabsAsync()
    {
        List<string> met = [.. await RefusalsAsync()];
        string[] tabs = await Browser.EvaluateAsync<string[]>(
            "[...document.querySelectorAll('#serviceTabs [data-service-tab]')].map(a => a.dataset.serviceTab)") ?? [];

        foreach (string tab in tabs)
        {
            await ClickAsync($"#serviceTabs [data-service-tab=\"{tab}\"]");
            await Task.Delay(1500);
            met.AddRange((await RefusalsAsync()).Select(r => $"{tab}: {r}"));
        }

        return [.. met];
    }

    /// <summary>
    /// The item page asks a reader for nothing they are refused — Server's settings and the stored symbology were asked of
    /// everybody and the refusal shown in red — and offers an outsider no Settings and no share control on another
    /// organization's item.
    /// </summary>
    [Fact]
    public async Task An_item_page_asks_its_reader_nothing_they_are_refused_and_offers_an_outsider_no_settings()
    {
        Assert.Equal(["default"], await TenantKeysAsync());
        string run = Guid.NewGuid().ToString("N")[..8];
        string key = "zzi" + run;
        string host = $"{key}.localhost";
        string reader = "zz_reader_" + run;
        string ours = "zz_ours_" + run;
        string theirs = "zz_theirs_" + run;

        try
        {
            (int s, _) = await AdminAsync(HttpMethod.Post, "/admin/hosted/define", JsonSerializer.Serialize(
                new { name = ours, geometryType = "Point", srid = 4326, fields = Array.Empty<object>() }));
            Assert.True(s == 201, $"Defining {ours} answered {s}.");
            (s, _) = await AdminAsync(HttpMethod.Put, $"/admin/services/{ours}/sharing?folder=hosted",
                JsonSerializer.Serialize(new { sharing = "organization" }));
            Assert.True(s == 200, $"Sharing {ours} with the organization answered {s}.");
            string token = await MemberAsync(reader, "user");

            // <b>A plain member at home</b>: every tab, and not one request the server refuses nor one red message.
            await OpenAsync($"/studio/#/service/hosted/{ours}", token);
            await WaitForAsync($"(document.getElementById('view-service')?.innerText || '').includes({JsonSerializer.Serialize(ours)})",
                "The item page did not draw for a plain member.");
            await Task.Delay(1500);
            string[] home = await WalkTabsAsync();
            Assert.True(home.Length == 0, "A plain member's item page asked for what they are refused: " + string.Join("; ", home));
            NothingWentWrong(await PageErrorsAsync());

            string? admin = await SecondOrganizationAsync(key, "Zz Item Partners", host);
            if (admin is null)
            {
                return;
            }

            (s, _) = await AtHostAsync(host, admin, HttpMethod.Post, "/admin/hosted/define",
                new { name = theirs, geometryType = "Point", srid = 4326, fields = Array.Empty<object>() });
            Assert.True(s == 201, $"Defining {theirs} at {host} answered {s}.");
            (s, _) = await AtHostAsync(host, admin, HttpMethod.Put, $"/admin/services/{theirs}/sharing?folder=hosted",
                new { sharing = "allorganizations" });
            Assert.True(s == 200, $"Sharing {theirs} with every organization answered {s}.");

            // <b>The same member, as an outsider there</b>, asking for Settings by address: it is not offered, nor is any
            // share control, and nothing is refused on the way.
            await OpenOnHostAsync(host, $"/studio/#/service/hosted/{theirs}?tab=settings", token);
            await WaitForAsync($"(document.getElementById('view-service')?.innerText || '').includes({JsonSerializer.Serialize(theirs)})",
                "The other organization's item page did not draw for an outsider.");
            await Task.Delay(1500);
            string[] tabs = await Browser.EvaluateAsync<string[]>(
                "[...document.querySelectorAll('#serviceTabs [data-service-tab]')].map(a => a.dataset.serviceTab)") ?? [];
            Assert.NotEmpty(tabs);
            Assert.DoesNotContain("settings", tabs);
            Assert.NotEqual("settings", await Browser.EvaluateAsync<string>(
                "document.getElementById('servicePagehead')?.dataset.tab || ''"));
            Assert.False(await Browser.EvaluateAsync<bool>(
                "[...document.querySelectorAll('[data-share]')].some(b => b.getClientRects().length > 0)"),
                "An outsider is offered a control that changes another organization's sharing.");
            Assert.True(await Browser.EvaluateAsync<bool>(
                "[...document.querySelectorAll('#view-service .pill')].some(p => p.getClientRects().length && p.textContent.trim() === 'All organizations')"),
                "The outsider is not told the item is shared with all organizations.");
            string[] away = await WalkTabsAsync();
            Assert.True(away.Length == 0, "An outsider's item page asked for what they are refused: " + string.Join("; ", away));
            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await PurgeAsync(key);
            await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{ours}?folder=hosted&drop=true");
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{reader}?deleteOwned=true");
        }
    }
}
