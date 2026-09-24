using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Shared domains on screen — ADR-087: the Domains screen, and a column's Values offering them.
/// </summary>
/// <remarks>
/// <b>The data is made through the real API and the screens' writes never reach it</b>, as everywhere in this
/// suite: what is asserted is what the pages show of a shared domain that really exists, and the requests they
/// send — in the order the server needs them.
/// </remarks>
public sealed class SharedDomainsScreenTests : ConsoleTest
{
    private sealed record Made(string Layer, string Folder, string Service, string Domain, string Unused);

    private async Task<Made> MakeAsync()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string layer = $"zz_domains_screen_{suffix}";
        string domain = $"zz Pipe material {suffix}";
        string unused = $"zz Unused {suffix}";

        (int defined, string design) = await AdminAsync(
            HttpMethod.Post,
            "/admin/hosted/define",
            JsonSerializer.Serialize(new
            {
                name = layer,
                geometryType = "Point",
                fields = new object[] { new { name = "material", type = "text", nullable = true } },
                sharing = "public",
            }));

        Assert.True(defined == 201, $"Defining {layer} answered {defined}: {design}");

        string[] parts = JsonDocument.Parse(design).RootElement
            .GetProperty("services").GetProperty("feature").GetString()!
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        (int set, string said) = await AdminAsync(
            HttpMethod.Put,
            $"/admin/layers/{Uri.EscapeDataString(layer)}/fields",
            JsonSerializer.Serialize(new
            {
                overrides = new object[]
                {
                    new
                    {
                        column = "material",
                        domain = new
                        {
                            type = "codedValue",
                            name = domain,
                            codedValues = new object[] { new { code = "CU", name = "Copper" }, new { code = "PVC", name = "PVC" } },
                        },
                    },
                },
            }));

        Assert.True(set == 200, $"Giving {layer} a domain answered {set}: {said}");

        (int created, string made) = await AdminAsync(
            HttpMethod.Post, "/admin/domains",
            JsonSerializer.Serialize(new { domain = new { type = "codedValue", name = unused, codedValues = new object[] { new { code = "X", name = "X" } } } }));

        Assert.True(created == 201, $"Creating {unused} answered {created}: {made}");

        return new Made(layer, parts[2], parts[3], domain, unused);
    }

    private async Task ForgetAsync(Made made)
    {
        await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{made.Service}?folder={made.Folder}&drop=true");

        (int listed, string body) = await AdminAsync(HttpMethod.Get, "/admin/domains");
        if (listed != 200) return;

        foreach (JsonElement d in JsonDocument.Parse(body).RootElement.GetProperty("domains").EnumerateArray())
        {
            string? name = d.GetProperty("name").GetString();
            if (name == made.Domain || name == made.Unused)
            {
                await AdminAsync(HttpMethod.Delete, $"/admin/domains/{d.GetProperty("id").GetString()}");
            }
        }
    }

    [Fact]
    public async Task The_domains_screen_says_where_each_is_used_and_deletes_only_what_nothing_uses()
    {
        (string token, _) = await SignInAsync();
        Made made = await MakeAsync();

        try
        {
            await OpenAsync("/studio/#/domains", token);

            string Row(string name) =>
                $"[...document.querySelectorAll('#domainRows tr')].find(r => r.querySelector('td.name')?.textContent === {JsonSerializer.Serialize(name)})";

            await WaitForAsync($"!!{Row(made.Domain)} && !!{Row(made.Unused)}", "The Domains screen does not list both domains.");

            // ---- the one in use says where, and offers no Delete that would be refused ----
            Assert.Contains("1 field", await Browser.EvaluateAsync<string>($"{Row(made.Domain)}.querySelector('summary')?.textContent || ''") ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains(made.Layer, await Browser.EvaluateAsync<string>($"{Row(made.Domain)}.querySelector('.domainuses')?.textContent || ''") ?? string.Empty, StringComparison.Ordinal);
            Assert.False(await Browser.EvaluateAsync<bool>($"!!{Row(made.Domain)}.querySelector('[data-domain-delete]')"), "Delete is offered on a domain in use.");
            Assert.Contains("In use", await Browser.EvaluateAsync<string>($"{Row(made.Domain)}.querySelector('.actions')?.textContent || ''") ?? string.Empty, StringComparison.Ordinal);

            // ---- the unused one is deleted, and the screen says so ----
            await Browser.EvaluateAsync<bool>("(() => { window.confirm = () => true; return true; })()");
            await Browser.EvaluateAsync<bool>($"(() => {{ {Row(made.Unused)}.querySelector('[data-domain-delete]').click(); return true; }})()");

            await WaitForAsync("document.getElementById('domainsSays').textContent.includes('was deleted')", "Deleting an unused domain announced nothing.");

            Assert.Contains(await WritesAsync(), w => w.StartsWith("DELETE", StringComparison.Ordinal) && w.Contains("/admin/domains/", StringComparison.Ordinal));
            Assert.Equal("polite", await Browser.EvaluateAsync<string>("document.getElementById('domainsSays').getAttribute('aria-live')"));

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await ForgetAsync(made);
        }
    }

    [Fact]
    public async Task A_columns_values_choose_a_shared_domain_and_its_values_are_changed_on_the_domains_screen()
    {
        (string token, _) = await SignInAsync();
        Made made = await MakeAsync();

        try
        {
            await OpenAsync($"/studio/#/layer/{Uri.EscapeDataString(made.Layer)}/fields", token);

            await WaitForAsync("!!document.querySelector('[data-field-values]')", "The Fields page drew no Values button.");
            await ClickAsync("[data-field-values]");

            await WaitForAsync("!!document.getElementById('valuesShared')", "The Values editor offers no shared domain.");

            // ---- the column's own is chosen, shown, and not editable here ----
            Assert.Contains(made.Domain, await Browser.EvaluateAsync<string>(
                "document.getElementById('valuesShared').selectedOptions[0]?.textContent || ''") ?? string.Empty, StringComparison.Ordinal);
            Assert.True(await Browser.EvaluateAsync<bool>("document.querySelector('[data-values-name=\"0\"]').readOnly"),
                "A shared domain's values can be typed into on a layer's page.");
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.querySelector('[data-values-remove], #valuesAdd')"),
                "A shared domain's values can be removed or added to on a layer's page.");
            Assert.Equal("valuesSharedSays", await Browser.EvaluateAsync<string>("document.getElementById('valuesShared').getAttribute('aria-describedby')"));

            // ---- a new list keeps what was typed while a shared one is looked at ----
            await Browser.EvaluateAsync<bool>("(() => { const s = document.getElementById('valuesShared'); s.value = ''; s.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");
            await Browser.EvaluateAsync<bool>("(() => { document.getElementById('valuesAdd').click(); return true; })()");
            await Browser.EvaluateAsync<bool>("(() => { document.querySelector('[data-values-code=\"0\"]').value = 'TYPED'; return true; })()");

            string sharedId = await Browser.EvaluateAsync<string>(
                $"[...document.getElementById('valuesShared').options].find(o => o.textContent.includes({JsonSerializer.Serialize(made.Domain)})).value") ?? string.Empty;

            await Browser.EvaluateAsync<bool>($"(() => {{ const s = document.getElementById('valuesShared'); s.value = '{sharedId}'; s.dispatchEvent(new Event('change', {{ bubbles: true }})); return true; }})()");
            await Browser.EvaluateAsync<bool>("(() => { const s = document.getElementById('valuesShared'); s.value = ''; s.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");

            Assert.Equal("TYPED", await Browser.EvaluateAsync<string>("document.querySelector('[data-values-code=\"0\"]')?.value || ''"));

            // ---- a new one under a taken name is refused at Done, not after Save ----
            await Browser.EvaluateAsync<bool>($"(() => {{ document.getElementById('valuesName').value = {JsonSerializer.Serialize(made.Domain)}; return true; }})()");
            await ClickAsync("#valuesDone");

            Assert.True(await Browser.EvaluateAsync<bool>("!!document.getElementById('valuesDone')"), "Done closed the editor over a taken name.");
            Assert.Contains("already named", await Browser.EvaluateAsync<string>("document.getElementById('valuesSharedSays').textContent") ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal("valuesName", await Browser.EvaluateAsync<string>("document.activeElement?.id || ''"));

            // ---- back to the shared one, and to the Domains screen to change it ----
            await Browser.EvaluateAsync<bool>($"(() => {{ const s = document.getElementById('valuesShared'); s.value = '{sharedId}'; s.dispatchEvent(new Event('change', {{ bubbles: true }})); return true; }})()");
            await ClickAsync("[data-values-edit-shared]");

            await WaitForAsync("!!document.getElementById('domEditName')", "Changing a shared domain did not open it on the Domains screen.");

            Assert.Equal(made.Domain, await Browser.EvaluateAsync<string>("document.getElementById('domEditName').value"));

            await Browser.EvaluateAsync<bool>("(() => { window.confirm = () => true; document.querySelector('[data-dom-name=\"1\"]').value = 'Polyvinyl chloride'; return true; })()");
            await ClickAsync("#domEditSave");

            await WaitForAsync("document.getElementById('domainsSays').textContent.includes('was saved')", "Saving a shared domain announced nothing.");

            Assert.Contains(await WritesAsync(), w => w.StartsWith("PUT", StringComparison.Ordinal) && w.Contains("/admin/domains/", StringComparison.Ordinal));
            Assert.DoesNotContain(await WritesAsync(), w => w.Contains("/fields", StringComparison.Ordinal));

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await ForgetAsync(made);
        }
    }
}
