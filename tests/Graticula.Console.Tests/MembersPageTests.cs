using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Members as Portal's Members page — ADR-190: searched, filtered and paged by the server, the verbs behind ⋯; a
/// member's profile at <c>#/user/{name}</c>; and every "new" a dialog that ✕ and Escape put away (ADR-189).
/// </summary>
public sealed class MembersPageTests : ConsoleTest
{
    private static string Me => Environment.GetEnvironmentVariable("GRATICULA_TEST_USER") ?? "root";

    [Fact]
    public async Task A_search_asks_the_server_and_the_list_shows_what_it_answered()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/members", token);
        await WaitForAsync(Shown("#memberQ"), "Members drew no search box.");
        await WaitForAsync("document.querySelectorAll('#members tr').length > 0", "Members listed nobody.");

        // Filters the server answers for: the roles and user types it knows, a status, a last login, a group.
        Assert.True(await Browser.EvaluateAsync<bool>("document.querySelectorAll('#mfRoles input[data-mf-role]').length > 0"),
            "No role filter was drawn.");
        Assert.True(await Browser.EvaluateAsync<bool>("document.querySelectorAll('#mfTypes input[data-mf-type]').length > 0"),
            "No user type filter was drawn.");

        await Browser.EvaluateAsync<bool>(
            "(() => { const q = document.getElementById('memberQ'); q.value = 'zz-nobody-at-all'; "
            + "q.dispatchEvent(new Event('input', { bubbles: true })); return true; })()");

        await WaitForAsync(
            "document.getElementById('memberCount').textContent.startsWith('0 members')",
            "A search that matches nobody did not say so.");

        await Browser.EvaluateAsync<bool>(
            $"(() => {{ const q = document.getElementById('memberQ'); q.value = {System.Text.Json.JsonSerializer.Serialize(Me)}; "
            + "q.dispatchEvent(new Event('input', { bubbles: true })); return true; })()");

        await WaitForAsync(
            $"[...document.querySelectorAll('#members tr a[href*=\"/user/\"]')].some(a => a.getAttribute('href').endsWith('/user/' + encodeURIComponent({System.Text.Json.JsonSerializer.Serialize(Me)})))",
            "Searching for the signed-in member did not list them with a link to their profile.");

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task A_rows_verbs_are_behind_its_menu_and_the_menu_opens_over_the_table()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/members", token);
        await WaitForAsync(Shown("#members details.rowmenu summary"), "A row has no ⋯.");

        await ClickAsync("#members details.rowmenu summary");
        await WaitForAsync(Shown("#members details.rowmenu[open] [data-member-remove]"), "⋯ did not show Remove member.");

        // The last item can be pressed where it is drawn: nothing (the table's scroll box) sits over it.
        Assert.True(await Browser.EvaluateAsync<bool>(
            "(() => { const b = document.querySelector('#members details.rowmenu[open] [data-member-remove]'); "
            + "const r = b.getBoundingClientRect(); const e = document.elementFromPoint(r.left + 8, r.top + r.height / 2); "
            + "return e === b || b.contains(e); })()"), "Remove member is drawn under something else.");

        NothingWentWrong(await PageErrorsAsync());
    }

    [Theory]
    [InlineData("members", "memberNew", "memberDialog", "mName")]
    [InlineData("roles", "roleNew", "roleDialog", "newRoleName")]
    [InlineData("signin", "appNew", "appDialog", "appTitle")]
    [InlineData("groups", "groupNew", "groupNewDialog", "newGroupTitle")]
    public async Task A_new_thing_is_asked_in_a_dialog_that_escape_puts_away(string screen, string opener, string dialog, string first)
    {
        (string token, _) = await SignInAsync();

        await OpenAsync($"/studio/#/{screen}", token);
        await WaitForAsync(Shown($"#{opener}"), $"#{opener} is not on screen.");
        await ClickAsync($"#{opener}");

        await WaitForAsync($"document.getElementById('{dialog}').open", $"#{opener} opened no dialog.");
        Assert.Equal(first, await Browser.EvaluateAsync<string>("document.activeElement?.id || ''"));

        await Browser.EvaluateAsync<bool>(
            $"(() => {{ document.getElementById('{dialog}').dispatchEvent(new Event('cancel')); "
            + $"document.getElementById('{dialog}').close(); return true; }})()");
        Assert.False(await Browser.EvaluateAsync<bool>($"document.getElementById('{dialog}').open"), "The dialog would not close.");

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task A_members_own_profile_is_theirs_to_edit_and_says_who_may_read_it()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync($"/studio/#/user/{Uri.EscapeDataString(Me)}", token);

        await WaitForAsync(Shown("#pfBio"), "One's own profile offers no bio to write.");
        Assert.True(await Browser.EvaluateAsync<bool>(Shown("#photoOpen")), "One's own profile offers no photo.");
        Assert.Equal(3, await Browser.EvaluateAsync<int>("document.querySelectorAll('#pfVisibility option').length"));
        Assert.False(string.IsNullOrWhiteSpace(await Browser.EvaluateAsync<string>("document.getElementById('pfVisibilityHint').textContent")),
            "The visibility says nothing about who may read the profile.");

        await ClickAsync("#photoOpen");
        await WaitForAsync("document.getElementById('photoDialog').open", "Add a photo opened no dialog.");
        Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('photoSave').disabled"),
            "Save is offered before a photo is chosen.");

        NothingWentWrong(await PageErrorsAsync());
    }
}
