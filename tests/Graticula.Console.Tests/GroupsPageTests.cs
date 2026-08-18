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
            string members = await Browser.EvaluateAsync<string>(
                "(document.getElementById('groupMembers')?.textContent || '')"
                + ".replace(/[ \\n\\r\\t]+/g, ' ').trim()") ?? string.Empty;

            Assert.Contains("owner", members, StringComparison.Ordinal);

            // And the capability is said in words rather than as a code.
            string capability = await Browser.EvaluateAsync<string>(
                "(document.getElementById('groupCapability')?.textContent || '')"
                + ".replace(/[ \\n\\r\\t]+/g, ' ').trim()") ?? string.Empty;

            Assert.Contains("read what is shared", capability, StringComparison.OrdinalIgnoreCase);

            Assert.Contains(
                "cannot be changed",
                capability,
                StringComparison.OrdinalIgnoreCase);

            // The sentence that stops the step people miss: sharing here is not enough.
            string hint = await Browser.EvaluateAsync<string>(
                "(document.getElementById('view-groups')?.textContent || '')"
                + ".replace(/[ \\n\\r\\t]+/g, ' ').trim()") ?? string.Empty;

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
