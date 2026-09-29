using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The Share dialog writes what the reader chose, and a share into a group reaches its members.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two defects the design review of 2026-09-30 reproduced, and no test covered the dialog at all.</b>
/// Choosing <i>Organization</i>, ticking a group and pressing Back put the radio back on <i>Owner</i>,
/// because the screen was redrawn from the stored scope; Save then wrote <c>private</c> under a success
/// toast. And <i>Owner</i> with a group ticked was saved as <c>private</c> plus a group row, which the
/// server does not read — group membership counts only for an item whose scope is <c>group</c> — so the
/// group's own Content tab said the share reached nobody.
/// </para>
/// <para>
/// <b>The request bodies are read by a wrapper this test installs</b>, because the harness's trap records
/// a write's method and address and not its JSON body, and the scope is in the body. The wrapper sits in
/// front of the trap, so nothing is sent to the server either way.
/// </para>
/// </remarks>
public sealed class ShareDialogTests : ConsoleTest
{
    private const string Probe = "zz_share_dialog_probe";

    private const string RecordBodies = """
        (() => {
          window.__bodies = [];
          const trap = window.fetch;
          window.fetch = (input, init) => {
            const method = (init && init.method) || "GET";
            if (method !== "GET") {
              window.__bodies.push(method + " " + String(input).split("?")[0] + " "
                + (typeof init.body === "string" ? init.body : ""));
            }
            return trap(input, init);
          };
          return true;
        })()
        """;

    /// <summary>The level chosen before visiting the group screen is the one Save writes.</summary>
    [Fact]
    public async Task The_chosen_level_survives_the_group_screen_and_is_what_Save_writes()
    {
        string[] writes = await ShareAsync(level: "organization");

        string? scope = writes.FirstOrDefault(w => w.Contains("/sharing", StringComparison.Ordinal));

        Assert.True(
            scope is null || scope.Contains("\"organization\"", StringComparison.Ordinal),
            $"Save wrote {scope} after Organization was chosen. Writes: {string.Join(" | ", writes)}");
    }

    /// <summary>Owner with a group ticked is stored as the group scope, with the group in place first.</summary>
    [Fact]
    public async Task Owner_with_a_group_is_saved_as_the_group_scope_after_the_group_is_added()
    {
        string[] writes = await ShareAsync(level: "private");

        int group = Array.FindIndex(writes, w =>
            w.StartsWith("PUT ", StringComparison.Ordinal)
            && w.Contains($"/admin/groups/{Probe}/items/", StringComparison.Ordinal));

        int scope = Array.FindIndex(writes, w => w.Contains("/sharing", StringComparison.Ordinal));

        Assert.True(group >= 0, $"The group was not added. Writes: {string.Join(" | ", writes)}");
        Assert.True(scope >= 0, $"The scope was not written. Writes: {string.Join(" | ", writes)}");

        Assert.Contains("\"group\"", writes[scope], StringComparison.Ordinal);

        Assert.True(
            group < scope,
            "The scope was set to group before any group was in place, which the server refuses: "
            + string.Join(" | ", writes));
    }

    /// <summary>
    /// Opens the dialog on a service not yet shared with the probe group, chooses a level, ticks the
    /// group, confirms with Done and saves; returns what was written.
    /// </summary>
    private async Task<string[]> ShareAsync(string level)
    {
        (string token, _) = await SignInAsync();

        (int made, string why) = await AdminAsync(
            HttpMethod.Post,
            "/admin/groups",
            JsonSerializer.Serialize(new { name = Probe, title = "Share dialog probe" }));

        Assert.True(made is 200 or 201 or 409, $"{made} {why}");

        try
        {
            await OpenAsync("/studio/#/content", token);

            await WaitForAsync(
                "!!document.querySelector('#contentRows button.pillbtn[data-share]')",
                "My content drew no sharing control to open the dialog from.");

            string service = await Browser.EvaluateAsync<string>(
                "document.querySelector('#contentRows button.pillbtn[data-share]').dataset.share")
                ?? string.Empty;

            await Browser.EvaluateAsync<bool>(RecordBodies);
            await ClickAsync($"#contentRows button.pillbtn[data-share=\"{service}\"]");

            await WaitForAsync(
                "!!document.querySelector('#shareBody input[name=shareScope]')",
                "The Share dialog never drew its levels.");

            await Browser.EvaluateAsync<bool>(
                $"(document.querySelector('#shareBody input[name=shareScope][value={level}]').click(), true)");

            await ClickAsync("#shareEditGroups");

            await WaitForAsync(
                $"!!document.querySelector('#shareRows input[data-share-group=\"{Probe}\"]')",
                "The group screen did not offer the probe group, which the signed-in account owns.");

            await Browser.EvaluateAsync<bool>(
                $"(() => {{ const t = document.querySelector('#shareRows input[data-share-group=\"{Probe}\"]');"
                + " if (!t.checked) t.click(); return true; })()");

            string done = await Browser.EvaluateAsync<string>(
                "document.getElementById('shareBack').textContent.trim()") ?? string.Empty;

            Assert.Equal("Done", done);

            await ClickAsync("#shareBack");

            await WaitForAsync(
                "!!document.querySelector('#shareBody input[name=shareScope]')",
                "Done did not return to the levels.");

            string shown = await Browser.EvaluateAsync<string>(
                "(document.querySelector('#shareBody input[name=shareScope]:checked') || {}).value || ''")
                ?? string.Empty;

            Assert.True(
                shown == level,
                $"{level} was chosen before the group screen and {shown} is chosen after it. The level "
                + "was redrawn from what the server holds, and Save would write that.");

            await ClickAsync("#shareSave");

            await WaitForAsync(
                "!document.getElementById('share').open",
                "Save did not close the dialog.");

            return await Browser.EvaluateAsync<string[]>("window.__bodies") ?? [];
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Probe}");
        }
    }
}
