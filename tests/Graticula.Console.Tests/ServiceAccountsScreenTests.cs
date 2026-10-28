using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Service accounts and their API keys on Organization › Settings › Security — ADR-200 condition 3, D-303.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built on 2026-10-18 with an API and no screen</b>, which left a key nobody could see, and a key nobody can see is a
/// key nobody revokes. What is asserted is what a reader needs: the accounts and their keys with when each was last
/// used, a key shown once and taken off the page when its dialog closes, and the administrator role never offered.
/// </para>
/// <para>
/// <b>Answered in the page</b>, because the harness traps every write: a small stand-in for <c>/admin/service-accounts</c>
/// answers the list, the keys and the writes, so the screen can be walked through a whole create-and-issue round. The
/// server's half is <c>AnApiKeyOpensWhatItsAccountMayTests</c>.
/// </para>
/// </remarks>
public sealed class ServiceAccountsScreenTests : ConsoleTest
{
    private const string Fake = """
    (() => {
      const real = window.fetch;
      window.__sa = {
        accounts: [{ name: "zz-sync", description: "Copies inspections", userType: "creator", roles: ["viewer"],
                     created: "2026-10-01T09:00:00Z", disabled: false, liveKeys: 1 }],
        keys: { "zz-sync": [{ id: "11111111-1111-1111-1111-111111111111", name: "old", created: "2026-10-01T09:00:00Z",
                              expires: null, revoked: null, lastUsed: null }] },
        sent: [],
      };
      const json = (status, body) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
      window.fetch = async (input, init) => {
        const url = typeof input === "string" ? input : input.url;
        const method = ((init && init.method) || "GET").toUpperCase();
        const path = new URL(url, location.href).pathname;
        if (!path.startsWith("/admin/service-accounts")) return real(input, init);
        window.__sa.sent.push(method + " " + path);
        const parts = path.split("/").slice(3).map(decodeURIComponent);
        if (parts.length === 0 && method === "GET") return json(200, { accounts: window.__sa.accounts });
        if (parts.length === 0 && method === "POST") {
          const b = JSON.parse(init.body);
          window.__sa.accounts.push({ ...b, created: new Date().toISOString(), disabled: false, liveKeys: 0 });
          window.__sa.keys[b.name] = [];
          return json(201, { name: b.name });
        }
        if (parts[1] === "keys" && method === "GET") return json(200, { account: parts[0], keys: window.__sa.keys[parts[0]] || [] });
        if (parts[1] === "keys" && method === "POST") {
          const b = JSON.parse(init.body);
          window.__sa.keys[parts[0]].push({ id: "22222222-2222-2222-2222-222222222222", name: b.name,
            created: new Date().toISOString(), expires: null, revoked: null, lastUsed: null });
          return json(201, { id: "22222222-2222-2222-2222-222222222222", name: b.name, key: "gak_zztestkeyshownonce" });
        }
        return json(200, {});
      };
      return true;
    })();
    """;

    private async Task OpenSecurityAsync()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/studio/signin", token);
        await WaitForAsync(Shown("#saNew"), "Organization › Settings › Security drew no service accounts for an administrator (D-303).");

        await Browser.EvaluateAsync<bool>(Fake);
        await Browser.EvaluateAsync<bool>("(loadServiceAccounts(), true)");
        await WaitForAsync("(document.getElementById('saRows')?.textContent || '').includes('zz-sync')", "The account list was not drawn.");
    }

    [Fact]
    public async Task An_account_shows_its_keys_and_a_new_key_is_shown_once()
    {
        await OpenSecurityAsync();

        // Its keys open under it, with when each was last used, and the button says it is open.
        Assert.Equal("false", await Browser.EvaluateAsync<string>("document.querySelector('[data-sa-keys=\"zz-sync\"]').getAttribute('aria-expanded')"));
        await ClickAsync("[data-sa-keys='zz-sync']");
        await WaitForAsync(Shown("#saKeysOf"), "Keys opened nothing.");
        string keys = await Browser.EvaluateAsync<string>("document.getElementById('saKeysOf').innerText") ?? string.Empty;
        Assert.Contains("old", keys, StringComparison.Ordinal);
        Assert.Contains("Until revoked", keys, StringComparison.Ordinal);
        Assert.Contains("Never", keys, StringComparison.Ordinal);
        Assert.Equal("true", await Browser.EvaluateAsync<string>("document.querySelector('[data-sa-keys=\"zz-sync\"]').getAttribute('aria-expanded')"));
        Assert.Equal("zz-sync", await Browser.EvaluateAsync<string>("document.activeElement?.dataset?.saKeys ?? ''"));

        // A lifetime outside 1 to 3650 days is said, and nothing is sent.
        await Browser.EvaluateAsync<bool>("(document.getElementById('saKeyName').value = 'nightly', document.getElementById('saKeyDays').value = '0', true)");
        await ClickAsync("#saKeyIssue");
        await WaitForAsync("(document.getElementById('saSays')?.textContent || '').includes('1 to 3650')", "A lifetime of 0 days was not refused in words.");
        Assert.Equal(0, await Browser.EvaluateAsync<int>("window.__sa.sent.filter(s => s.startsWith('POST')).length"));

        // A key: shown once, selected, in a dialog that holds the page; gone from the page when it closes.
        await Browser.EvaluateAsync<bool>("(document.getElementById('saKeyDays').value = '30', true)");
        await ClickAsync("#saKeyIssue");
        await WaitForAsync("document.getElementById('saKeyIssued').open", "Creating a key showed nothing.");
        Assert.Equal("gak_zztestkeyshownonce", await Browser.EvaluateAsync<string>("document.getElementById('saKeyText').value"));
        Assert.Equal("saKeyText", await Browser.EvaluateAsync<string>("document.activeElement?.id ?? ''"));

        // Escape does not close it: the key would stay in the field with nothing having said it was copied.
        await Browser.CallAsync("Input.dispatchKeyEvent", new { type = "keyDown", key = "Escape", code = "Escape", windowsVirtualKeyCode = 27 });
        await Browser.CallAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "Escape", code = "Escape", windowsVirtualKeyCode = 27 });
        Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('saKeyIssued').open"), "Escape closed the once-only key dialog.");

        await ClickAsync("#saKeyDone");
        await WaitForAsync("!document.getElementById('saKeyIssued').open", "The key's dialog did not close.");
        Assert.Equal(string.Empty, await Browser.EvaluateAsync<string>("document.getElementById('saKeyText').value"));
        Assert.DoesNotContain("gak_zztestkeyshownonce", await Browser.EvaluateAsync<string>("document.body.innerHTML") ?? string.Empty, StringComparison.Ordinal);
        await WaitForAsync("(document.getElementById('saSays')?.textContent || '').includes('is live')", "Nothing said the key is live.");
        Assert.Contains("nightly", await Browser.EvaluateAsync<string>("document.getElementById('saKeysOf').innerText") ?? string.Empty, StringComparison.Ordinal);

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task A_new_account_is_never_offered_the_administrator_role()
    {
        await OpenSecurityAsync();

        await ClickAsync("#saNew");
        await WaitForAsync("document.getElementById('saDialog').open", "New service account opened nothing.");
        await WaitForAsync("document.querySelectorAll('#saRole option').length > 1", "The role list was not filled.");

        Assert.False(await Browser.EvaluateAsync<bool>("[...document.querySelectorAll('#saRole option')].some(o => o.value === 'administrator')"),
            "The administrator role is offered to a machine, which the server refuses by name.");
        Assert.Equal("saName", await Browser.EvaluateAsync<string>("document.activeElement?.id ?? ''"));

        // A name the server would refuse is said in the dialog, before anything is sent.
        await Browser.EvaluateAsync<bool>("(document.getElementById('saName').value = 'has spaces', true)");
        await ClickAsync("#saSave");
        await WaitForAsync(Shown("#saFormSays"), "A name with spaces was not refused in the dialog.");
        Assert.Equal(0, await Browser.EvaluateAsync<int>("window.__sa.sent.filter(s => s.startsWith('POST')).length"));

        // A good one is made, and its keys open under it for the first key.
        await Browser.EvaluateAsync<bool>("(document.getElementById('saName').value = 'zz-new', true)");
        await ClickAsync("#saSave");
        await WaitForAsync("!document.getElementById('saDialog').open", "Create did not close the dialog.");
        await WaitForAsync(Shown("#saKeyName"), "The new account's keys did not open for its first key.");
        Assert.Equal("saKeyName", await Browser.EvaluateAsync<string>("document.activeElement?.id ?? ''"));

        NothingWentWrong(await PageErrorsAsync());
    }
}
