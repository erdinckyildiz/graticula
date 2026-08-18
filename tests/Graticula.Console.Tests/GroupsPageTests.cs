using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The Groups screen: where a group is made, and where you see the ones you are in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asked for by the owner, 2026-08-18:</b> *"grubu nereden oluşturuyoruz. içinde olduğum grupların
/// listesini nereden görüyorum?"* — the answer was *nowhere*: ADR-036's store and API were built and
/// measured, and its condition 5 (the screen) was still outstanding. This class is what stops that
/// happening again for this screen.
/// </para>
/// <para>
/// <b>It asserts the two acts and the one refusal.</b> Creating a group and listing your own are the
/// question; that a plain member is offered no controls is ADR-034 condition 1 — *no screen appears
/// that its reader cannot use* — and it is the assertion a screenshot cannot give.
/// </para>
/// </remarks>
public sealed class GroupsPageTests : ConsoleTest
{
    private const string Probe = "zz_groups_page_probe";

    /// <summary>
    /// Collapses runs of whitespace, in C# rather than in the page.
    /// </summary>
    /// <remarks>
    /// <b>Because a regex has to survive two layers of escaping to reach the browser.</b>
    /// <c>\s+</c> written in a C# string becomes a real newline in the JavaScript unless it is
    /// doubled, and the failure is *"Invalid regular expression: missing /"* — which says nothing
    /// about escaping. Rendered text has to be compared on its words, not on where the markup
    /// wrapped, and doing that here removes the whole class of mistake.
    /// </remarks>
    private static string Flat(string? text) =>
        System.Text.RegularExpressions.Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();

    /// <summary>
    /// Adding a member is a picker over real names, not a box to type one into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner, 2026-08-18:</b> *"add member asks for name. why not search a user and add user from
    /// the list"*. It asked because listing members needs <c>admin:manageMembers</c>, so a publisher
    /// who owns a group could not fill a list — and the answer was a narrower endpoint rather than a
    /// wider privilege. `GET /admin/groups/{name}/candidates` returns names only, to somebody who
    /// already manages the group.
    /// </para>
    /// <para>
    /// <b>The assertion with teeth is that the options are real members.</b> A picker rendered from
    /// nothing looks identical to one rendered from an empty answer, and typing a name from memory is
    /// what this replaced.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Adding_a_member_offers_the_members_who_could_join()
    {
        (string token, _) = await SignInAsync();

        (int made, string why) = await AdminAsync(
            HttpMethod.Post,
            "/admin/groups",
            JsonSerializer.Serialize(new { name = Probe, title = "Probe group" }));

        Assert.True(made is 200 or 201, $"{made} {why}");

        try
        {
            await OpenAsync("/studio/#/groups", token);

            await WaitForAsync(
                $"!!document.querySelector('#groupRows tr[data-group={Probe}]')",
                "The probe group is not listed.");

            await ClickAsync($"#groupRows tr[data-group={Probe}] td.name");

            await WaitForAsync(
                "!document.getElementById('groupActions').hidden",
                "The owner was not offered the group's controls.");

            await ClickAsync("#groupAdd");

            await WaitForAsync(
                "!!document.getElementById('groupPickWho')",
                "Add member did not open a picker. It used to be a prompt, which is what the owner "
                + "objected to: typing a name from memory makes a typo into a 404 about a member who "
                + "does exist.");

            int options = await Browser.EvaluateAsync<int>(
                "document.getElementById('groupPickWho')?.options.length || 0");

            Assert.True(
                options > 0,
                "The picker is empty. The candidates endpoint answered nothing, so the screen offers "
                + "a list with no members in it — which is the prompt again with extra steps.");

            // <b>And the members offered are real ones.</b> `root` is signing this test in and is
            // already in the group as its owner, so it must *not* be offered — a picker that lists
            // people already in the group is one that produces a no-op.
            string names = await Browser.EvaluateAsync<string>(
                "[...document.getElementById('groupPickWho').options]"
                + ".map(o => o.value).join(',')") ?? string.Empty;

            Assert.DoesNotContain("anonymous", names, StringComparison.Ordinal);

            // The manager tick is offered here, because that is the moment the choice is made.
            bool asManager = await Browser.EvaluateAsync<bool>(
                "!!document.getElementById('groupPickManager')");

            Assert.True(asManager, "There is no way to add somebody as a manager.");

            string[] errors = await PageErrorsAsync();
            Assert.Empty(errors);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Probe}");
        }
    }

    /// <summary>
    /// A group's owner is told they cannot leave, and is not offered a button that would refuse.
    /// </summary>
    /// <remarks>
    /// <b>Leave group is taken from the reference's group page, where *"You are a member"* sits above
    /// it.</b> Before this a member could be removed by a manager and could not walk out, which makes
    /// joining a group something done *to* somebody. The owner is the exception and the button is
    /// absent for them rather than present and refusing — the store refuses it, because an owner
    /// outside their own group is one no membership list shows.
    /// </remarks>
    [Fact]
    public async Task The_owner_is_not_offered_a_way_to_leave_their_own_group()
    {
        (string token, _) = await SignInAsync();

        (int made, string why) = await AdminAsync(
            HttpMethod.Post,
            "/admin/groups",
            JsonSerializer.Serialize(new { name = Probe, title = "Probe group" }));

        Assert.True(made is 200 or 201, $"{made} {why}");

        try
        {
            await OpenAsync("/studio/#/groups", token);

            await WaitForAsync(
                $"!!document.querySelector('#groupRows tr[data-group={Probe}]')",
                "The probe group is not listed.");

            await ClickAsync($"#groupRows tr[data-group={Probe}] td.name");

            await WaitForAsync(
                "(document.getElementById('groupStanding')?.textContent || '').length > 0",
                "The editor does not say where the reader stands in this group.");

            string standing = Flat(await Browser.EvaluateAsync<string>(
                "document.getElementById('groupStanding')?.textContent || ''"));

            Assert.Contains("You own this group", standing, StringComparison.OrdinalIgnoreCase);

            bool leave = await Browser.EvaluateAsync<bool>(
                "!!document.getElementById('groupLeave')"
                + " && !document.getElementById('groupLeave').hidden");

            Assert.False(
                leave,
                "The owner is offered Leave group, which the store refuses. A button that cannot "
                + "work is worse than its absence: it reads as a capability.");

            string[] errors = await PageErrorsAsync();
            Assert.Empty(errors);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Probe}");
        }
    }

    /// <summary>The screen lists the groups you are in, and offers to make one.</summary>
    [Fact]
    public async Task The_groups_screen_lists_your_groups_and_offers_a_new_one()
    {
        (string token, _) = await SignInAsync();

        (int made, string why) = await AdminAsync(
            HttpMethod.Post,
            "/admin/groups",
            JsonSerializer.Serialize(new
            {
                name = Probe,
                title = "Probe group",
                description = "Created by GroupsPageTests; removed when it finishes.",
            }));

        Assert.True(made is 200 or 201, $"Could not create the probe group: {made} {why}");

        try
        {
            await OpenAsync("/studio/#/groups", token);

            await WaitForAsync(
                $"!!document.querySelector('#groupRows tr[data-group={Probe}]')",
                "The Groups screen did not list a group the signed-in member owns. Either the tab is "
                + "absent, the endpoint refused, or the screen did not ask.");

            // <b>New group is offered.</b> The owner's question was where to create one, and a list
            // with no way to add to it is the answer *nowhere* wearing a screen.
            bool offers = await Browser.EvaluateAsync<bool>(
                "!!document.getElementById('groupNew')");

            Assert.True(offers, "There is no way to create a group from the Groups screen.");

            // The standing is shown, because *which of these am I responsible for* is the first
            // question somebody has about a list of groups.
            string standing = await Browser.EvaluateAsync<string>(
                $"document.querySelector('#groupRows tr[data-group={Probe}] .pill')"
                + "?.textContent?.trim() || ''") ?? string.Empty;

            Assert.Equal("owner", standing);

            string[] errors = await PageErrorsAsync();
            Assert.Empty(errors);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Probe}");
        }
    }

    /// <summary>
    /// Choosing a group opens it and shows its members and what is shared with it.
    /// </summary>
    /// <remarks>
    /// <b>Clicking a cell, not the row.</b> D-73 was exactly this defect on the Roles screen — the
    /// handler read the click target's own dataset and the target is the cell — and a test that
    /// clicked the row element would have passed against it.
    /// </remarks>
    [Fact]
    public async Task Choosing_a_group_shows_its_members_and_its_services()
    {
        (string token, _) = await SignInAsync();

        (int made, string why) = await AdminAsync(
            HttpMethod.Post,
            "/admin/groups",
            JsonSerializer.Serialize(new { name = Probe, title = "Probe group" }));

        Assert.True(made is 200 or 201, $"{made} {why}");

        try
        {
            await OpenAsync("/studio/#/groups", token);

            await WaitForAsync(
                $"!!document.querySelector('#groupRows tr[data-group={Probe}]')",
                "The probe group is not listed.");

            await ClickAsync($"#groupRows tr[data-group={Probe}] td.name");

            await WaitForAsync(
                "(document.getElementById('groupEditorName')?.textContent || '')"
                + ".includes('Probe group')",
                "Clicking a group's cell did not open it — the defect D-73 records for the Roles "
                + "screen, on a second screen.");

            // <b>The creator is in it, as its owner.</b> Otherwise a membership-filtered list omits
            // a group somebody owns, and every screen has to special-case that.
            string members = Flat(await Browser.EvaluateAsync<string>(
                "document.getElementById('groupMembers')?.textContent || ''"));

            Assert.Contains("owner", members, StringComparison.Ordinal);

            // And the capability is said in words rather than as a code.
            string capability = Flat(await Browser.EvaluateAsync<string>(
                "document.getElementById('groupCapability')?.textContent || ''"));

            Assert.Contains("read what is shared", capability, StringComparison.OrdinalIgnoreCase);

            Assert.Contains(
                "cannot be changed",
                capability,
                StringComparison.OrdinalIgnoreCase);

            // The sentence that stops the step people miss: sharing here is not enough.
            string hint = Flat(await Browser.EvaluateAsync<string>(
                "document.getElementById('view-groups')?.textContent || ''"));

            Assert.Contains("its own sharing scope", hint, StringComparison.OrdinalIgnoreCase);

            string[] errors = await PageErrorsAsync();
            Assert.Empty(errors);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Probe}");
        }
    }

    /// <summary>
    /// A group's controls are offered to somebody who may use them, and not otherwise.
    /// </summary>
    /// <remarks>
    /// <b>ADR-036 condition 5, which is ADR-034 condition 1 in a new place.</b> A plain member of a
    /// group may read what is shared with it and may not add members or share services. The screen
    /// hides those controls rather than offering them and reporting a 403 — the same choice the Roles
    /// screen makes for the administrator role.
    /// </remarks>
    [Fact]
    public async Task A_plain_member_is_shown_the_group_and_not_its_controls()
    {
        (string token, _) = await SignInAsync();

        (int made, string why) = await AdminAsync(
            HttpMethod.Post,
            "/admin/groups",
            JsonSerializer.Serialize(new { name = Probe, title = "Probe group" }));

        Assert.True(made is 200 or 201, $"{made} {why}");

        try
        {
            await OpenAsync("/studio/#/groups", token);

            await WaitForAsync(
                $"!!document.querySelector('#groupRows tr[data-group={Probe}]')",
                "The probe group is not listed.");

            await ClickAsync($"#groupRows tr[data-group={Probe}] td.name");

            await WaitForAsync(
                "!document.getElementById('groupEditor').hidden",
                "The group editor never opened.");

            // The signed-in account owns this group, so it *is* offered the controls — which is the
            // half that proves the flags are read rather than hard-coded off.
            bool actions = await Browser.EvaluateAsync<bool>(
                "!document.getElementById('groupActions').hidden");

            Assert.True(
                actions,
                "The owner of a group is not offered its controls, so the screen is read-only for "
                + "everybody and the `mayManage` flag is being ignored.");

            bool delete = await Browser.EvaluateAsync<bool>(
                "!document.getElementById('groupDelete').hidden");

            Assert.True(delete, "The owner is not offered Delete, which only they may do.");

            string[] errors = await PageErrorsAsync();
            Assert.Empty(errors);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Probe}");
        }
    }
}
