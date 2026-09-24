using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The server's settings on a screen, and a service's page size as one control — V-70, ADR-084.
/// </summary>
/// <remarks>
/// <para>
/// <b>The two faults every new screen here has shipped with are asserted first</b> — a redraw that drops
/// focus, and a result that arrives in a paragraph nobody announces — and the first-run state is what is
/// asserted before anything is pressed, because a deployment that never set a page size is every deployment
/// on its first day. Writes never reach the server in this suite; what is asserted is the request the
/// screen sends and what it does after.
/// </para>
/// <para>
/// <b>Rewritten after the design review of 2026-09-24.</b> The first version held the value in force in
/// the box, so Save pressed by habit stored the default as the operator's own; and it opened the Limits page
/// under Studio, where the page is not offered — the box it asserted on was in the markup and on no screen.
/// </para>
/// </remarks>
[Collection("server ground")]
public sealed class SettingsScreenTests : ConsoleTest
{
    private const string Box = "document.getElementById('setPageSize')";

    private const string Says = "document.getElementById('setSays')";

    [Fact]
    public async Task Settings_shows_where_the_page_size_comes_from_and_saves_only_what_was_typed()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/settings", token);

        await WaitForAsync(
            $"/^[0-9]+$/.test({Box}?.placeholder || '')"
            + " && (document.getElementById('setPageSizeSource')?.textContent || '').length > 0",
            "The Settings screen drew no default, or did not say where the page size comes from.");

        // ---- where it lives: a named link in Server's sidebar ----
        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "[...document.querySelectorAll('a[href$=\"#/settings\"]')].some(a => (a.getAttribute('aria-label') || a.textContent).includes('Settings'))"),
            "No link in the page is named Settings, so an administrator — or a screen reader at rail width — cannot find it.");

        // ---- the first-run state: an empty box over the default, a sentence saying so, no reset ----
        Assert.Equal(string.Empty, await Browser.EvaluateAsync<string>($"{Box}.value"));
        Assert.Contains(
            "Not set here, so the server uses its default",
            await Browser.EvaluateAsync<string>("document.getElementById('setPageSizeSource').textContent") ?? string.Empty,
            StringComparison.Ordinal);
        Assert.True(
            await Browser.EvaluateAsync<bool>("document.getElementById('setReset').hidden"),
            "Use the default is offered when nothing was set here, so it would do nothing.");
        Assert.Equal("setPageSizeSource", await Browser.EvaluateAsync<string>($"{Box}.getAttribute('aria-describedby')"));

        // ---- Save on the empty box of a first run stores nothing: the habit the review caught ----
        await ClickAsync("#setSave");

        Assert.Empty(await WritesAsync());
        Assert.Contains("already the server's default", await Browser.EvaluateAsync<string>($"{Says}.textContent") ?? string.Empty, StringComparison.Ordinal);

        // ---- a page size that is not one: nothing is sent, it is marked as a refusal, focus stays ----
        await Browser.EvaluateAsync<bool>($"(() => {{ {Box}.value = '0'; return true; }})()");
        await ClickAsync("#setSave");

        Assert.Empty(await WritesAsync());
        Assert.Contains("whole number", await Browser.EvaluateAsync<string>($"{Says}.textContent") ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("true", await Browser.EvaluateAsync<string>($"{Box}.getAttribute('aria-invalid') || ''"));
        Assert.True(await Browser.EvaluateAsync<bool>($"{Says}.classList.contains('bad-inline')"), "A refusal looks like the help text above it.");
        Assert.Equal("setPageSize", await Browser.EvaluateAsync<string>("document.activeElement?.id || ''"));

        // ---- typing clears a sentence about a value that is no longer there ----
        await Browser.EvaluateAsync<bool>($"(() => {{ {Box}.value = '25'; {Box}.dispatchEvent(new Event('input', {{ bubbles: true }})); return true; }})()");
        Assert.Equal(string.Empty, await Browser.EvaluateAsync<string>($"{Says}.textContent"));
        Assert.Equal(string.Empty, await Browser.EvaluateAsync<string>($"{Box}.getAttribute('aria-invalid') || ''"));

        // ---- Enter saves: the request, and an announcement in a live region ----
        await Browser.EvaluateAsync<bool>($"(() => {{ {Box}.value = '250'; {Box}.focus(); return true; }})()");

        // Dispatched at the focused box, as PublishScreenTests presses keys: the screen reads the event.
        await Browser.EvaluateAsync<bool>("""
            (() => {
              document.activeElement.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
              return true;
            })()
            """);

        await WaitForAsync($"{Says}.textContent.startsWith('Saved')", "Saving a page size announced nothing.");

        Assert.Contains(
            await WritesAsync(),
            w => w.StartsWith("PUT", StringComparison.Ordinal) && w.Contains("/admin/settings", StringComparison.Ordinal));
        Assert.Equal("polite", await Browser.EvaluateAsync<string>($"{Says}.getAttribute('aria-live')"));
        Assert.False(await Browser.EvaluateAsync<bool>($"{Says}.classList.contains('bad-inline')"), "A success is marked as a refusal.");

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task The_map_ground_is_an_ordered_list_top_first_and_the_services_do_not_move()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/settings", token);

        await WaitForAsync(
            "document.querySelectorAll('#groundList input[type=checkbox]').length >= 3",
            "The Map ground card listed fewer than three tile services — the fixture has several, private ones included.");

        const string Available = "[...document.querySelectorAll('#groundList input')].map(i => i.value)";
        const string Stack = "[...document.querySelectorAll('#groundStack .groundname')].map(n => n.textContent)";

        string[] names = await Browser.EvaluateAsync<string[]>(Available) ?? Array.Empty<string>();

        // ---- the first run: nothing chosen, said in words, nothing to undo or clear ----
        Assert.Empty(await Browser.EvaluateAsync<string[]>(Stack) ?? Array.Empty<string>());
        Assert.Contains("OpenStreetMap", await Browser.EvaluateAsync<string>("document.getElementById('groundOrder').textContent") ?? string.Empty, StringComparison.Ordinal);
        Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('groundClear').hidden"), "Use OpenStreetMap is offered when it is already the ground.");
        Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('groundUndo').hidden"), "Undo is offered with nothing changed.");

        // Every box is named by its own label.
        Assert.Equal(0, await Browser.EvaluateAsync<int>("[...document.querySelectorAll('#groundList input')].filter(i => !document.querySelector(`label[for=\"${i.id}\"]`)).length"));

        // ---- Save with nothing chosen stores nothing ----
        await ClickAsync("#groundSave");
        Assert.DoesNotContain(await WritesAsync(), w => w.Contains("/admin/settings/ground", StringComparison.Ordinal));

        // ---- ticking adds on top; the available list does not move ----
        await ClickAsync($"#groundList input[value=\"{names[1]}\"]");
        await ClickAsync($"#groundList input[value=\"{names[0]}\"]");

        Assert.Equal([names[0], names[1]], await Browser.EvaluateAsync<string[]>(Stack) ?? Array.Empty<string>());
        Assert.Equal(names, await Browser.EvaluateAsync<string[]>(Available));
        Assert.Contains("Not saved yet", await Browser.EvaluateAsync<string>("document.getElementById('groundOrder').textContent") ?? string.Empty, StringComparison.Ordinal);
        Assert.False(await Browser.EvaluateAsync<bool>("document.getElementById('groundUndo').hidden"), "A change that is not saved offers no way back.");

        // ---- moving: the order changes, focus stays with the service, the move is announced ----
        await ClickAsync($"#groundStack [data-ground=\"{names[1]}\"][data-ground-move=up]");

        Assert.Equal([names[1], names[0]], await Browser.EvaluateAsync<string[]>(Stack) ?? Array.Empty<string>());
        Assert.Equal(names[1], await Browser.EvaluateAsync<string>("document.activeElement?.dataset?.ground || ''"));
        Assert.Contains("1 of 2", await Browser.EvaluateAsync<string>("document.getElementById('groundSays').textContent") ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("polite", await Browser.EvaluateAsync<string>("document.getElementById('groundSays').getAttribute('aria-live')"));

        // The top row cannot go up, and says what it is by name to a screen reader.
        Assert.True(await Browser.EvaluateAsync<bool>($"document.querySelector('#groundStack [data-ground=\"{names[1]}\"][data-ground-move=up]').disabled"));
        Assert.Equal($"Move {names[0]} up", await Browser.EvaluateAsync<string>($"document.querySelector('#groundStack [data-ground=\"{names[0]}\"][data-ground-move=up]').getAttribute('aria-label')"));

        // ---- Save sends the ground and announces it, first at the bottom ----
        await ClickAsync("#groundSave");

        await WaitForAsync(
            "document.getElementById('groundSays').textContent.startsWith('Saved')",
            "Saving the ground announced nothing.");

        Assert.Contains(await WritesAsync(), w => w.StartsWith("PUT", StringComparison.Ordinal) && w.Contains("/admin/settings/ground", StringComparison.Ordinal));
        Assert.Contains($"{names[0]}, then {names[1]}", await Browser.EvaluateAsync<string>("document.getElementById('groundSays').textContent") ?? string.Empty, StringComparison.Ordinal);

        // The page size's sentence is not the ground's.
        Assert.Equal(string.Empty, await Browser.EvaluateAsync<string>($"{Says}.textContent"));

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task A_private_tile_service_is_offered_and_its_caution_shows_before_it_is_ticked()
    {
        (string token, _) = await SignInAsync();

        const string Private = "zz_ground_screen_private";

        try
        {
            (int published, string said) = await PublishOneAsync(Private, Private);
            Assert.True(published is 200 or 201, $"publishing {Private}: {published} {said}");

            await OpenAsync("/server/#/settings", token);

            await WaitForAsync(
                $"[...document.querySelectorAll('#groundList input')].some(i => i.value.endsWith('{Private}'))",
                "A private tile service is not offered as a ground: the list was read as somebody who is not signed in.");

            string caution = await Browser.EvaluateAsync<string>(
                $"(() => {{ const i = [...document.querySelectorAll('#groundList input')].find(i => i.value.endsWith('{Private}')); return document.getElementById(i.getAttribute('aria-describedby') || '')?.textContent || ''; }})()")
                ?? string.Empty;

            Assert.Contains("get the ground without it", caution, StringComparison.Ordinal);

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(System.Net.Http.HttpMethod.Delete, $"/admin/layers/{Private}");
            await AdminAsync(System.Net.Http.HttpMethod.Delete, $"/admin/featureservices/{Private}");
        }
    }

    [Fact]
    public async Task A_service_has_one_page_size_and_its_empty_box_says_the_servers()
    {
        (string token, _) = await SignInAsync();

        (string Folder, string[] Services)[] folders = await FoldersWithServicesAsync();
        (_, string[] services) = folders.FirstOrDefault(f => f.Services.Length > 0);

        Assert.False(services is null or { Length: 0 }, "No service anywhere, so there is no Limits page to open.");

        // Server's, where Limits is offered; the names are qualified already, so the address is the name.
        await OpenAsync(
            "/server/#/service/" + string.Join("/", Array.ConvertAll(services![0].Split('/'), Uri.EscapeDataString)),
            token);

        await WaitForAsync(Shown("#serviceNav a[data-service-page=limits]"), "The service has no Limits page in Server.");
        await ClickAsync("#serviceNav a[data-service-page=limits]");

        await WaitForAsync(Shown("#capMaxRows"), "The page size box is not on the screen after opening Limits.");

        // <b>Waiting for what only the `GET` can set</b> — the markup's placeholder is empty.
        await WaitForAsync(
            "/^[0-9]+$/.test(document.getElementById('capMaxRows')?.placeholder || '')"
            + " && /[0-9]/.test(document.getElementById('capServerPage')?.textContent || '')",
            "The empty page size box never showed the server's page size, in its placeholder and in the sentence under it.");

        Assert.False(
            await Browser.EvaluateAsync<bool>("!!document.getElementById('capDefRows')"),
            "The Limits page still offers a default page beside the page size — the two numbers V-70 made one.");

        Assert.Equal("capPageSays", await Browser.EvaluateAsync<string>("document.getElementById('capMaxRows').getAttribute('aria-describedby')"));

        // Every box on the page is named by a label, not by its placeholder.
        Assert.Equal(
            0,
            await Browser.EvaluateAsync<int>(
                "[...document.querySelectorAll('#page-limits input[type=number]')].filter(i => !document.querySelector(`label[for=\"${i.id}\"]`)).length"));

        // ---- a page size that is not one is refused before anything is sent, with focus in the box ----
        await Browser.EvaluateAsync<bool>("(() => { document.getElementById('capMaxRows').value = '0'; return true; })()");
        await ClickAsync("#servicePagesBody [data-service-save]");

        await WaitForAsync(
            "document.activeElement?.id === 'capMaxRows'",
            "A page size of 0 did not put focus back in its box.");

        Assert.DoesNotContain(await WritesAsync(), w => w.Contains("/capabilities", StringComparison.Ordinal));

        NothingWentWrong(await PageErrorsAsync());
    }
}
