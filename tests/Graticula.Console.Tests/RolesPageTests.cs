using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The roles screen: two sections, counts that move, and set-from-existing-role.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-035 §4f, shaped by the reference the owner named</b> and by the control they marked in the
/// screenshot. Asserted in a browser because every part of it is a rendering decision: which section
/// a privilege lands in, whether a count follows a tick, and whether the administrator's boxes are
/// disabled rather than merely refused on save.
/// </para>
/// <para>
/// <b>It reads and copies; it does not save.</b> Saving would edit the roles of the server the whole
/// suite runs against, and the only role safe to edit is one this test made — which
/// <c>RoleDirectoryTests</c> already does against a throwaway schema. What is under test here is the
/// screen.
/// </para>
/// </remarks>
public sealed class RolesPageTests : ConsoleTest
{
    /// <summary>The screen lists the roles and both privilege sections.</summary>
    [Fact]
    public async Task The_roles_screen_shows_both_sections_and_the_privilege_catalogue()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/roles", token);

        await WaitForAsync(
            "document.querySelectorAll('#roleRows tr[data-role]').length >= 5",
            "The roles screen did not list the five roles ADR-018 §3c seeds, so either the endpoint "
            + "refused or the screen did not ask.");

        // <b>Two sections, and a privilege in neither would be one no screen offers.</b>
        int sections = await Browser.EvaluateAsync<int>(
            "document.querySelectorAll('#rolePrivileges .rolesection').length");

        Assert.Equal(2, sections);

        int boxes = await Browser.EvaluateAsync<int>(
            "document.querySelectorAll('#rolePrivileges input[data-privilege]').length");

        Assert.True(
            boxes >= 18,
            $"The editor offers {boxes} privileges and the catalogue has at least eighteen. A "
            + "privilege with no tick is a grant no operator can make.");

        // The group privileges ADR-035 §4c added must be among them, or the decision has a screen
        // that cannot express it.
        string names = await Browser.EvaluateAsync<string>(
            "[...document.querySelectorAll('#rolePrivileges input[data-privilege]')]"
            + ".map(b => b.dataset.privilege).join(' ')") ?? string.Empty;

        foreach (string group in new[]
        {
            "groups:create", "groups:deleteOwn", "groups:manageMembers", "groups:shareTo",
        })
        {
            Assert.Contains(group, names, StringComparison.Ordinal);
        }

        string[] errors = await PageErrorsAsync();
        Assert.Empty(errors);
    }

    /// <summary>
    /// The administrator's boxes are disabled, and its Save button is gone.
    /// </summary>
    /// <remarks>
    /// <b>ADR-035 condition 5, and §4b made visible.</b> The store refuses the write anyway; a
    /// screen that let somebody tick and press Save would be one that reports a failure for
    /// something it should never have offered. The alternative — hiding the administrator's
    /// privileges — would remove the only place an operator can see what it holds.
    /// </remarks>
    [Fact]
    public async Task The_administrator_can_be_read_and_not_edited()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/roles", token);

        await WaitForAsync(
            "!!document.querySelector('#roleRows tr[data-role=administrator]')",
            "The administrator role is not listed.");

        await ClickAsync("#roleRows tr[data-role='administrator']");

        await WaitForAsync(
            "(document.getElementById('roleEditorName')?.textContent || '')"
            + ".includes('administrator')",
            "Choosing the administrator did not open its editor.");

        int editable = await Browser.EvaluateAsync<int>(
            "document.querySelectorAll("
            + "'#rolePrivileges input[data-privilege]:not([disabled])').length");

        Assert.Equal(0, editable);

        // It still shows what it holds — the ticks are there, just not clickable.
        int ticked = await Browser.EvaluateAsync<int>(
            "document.querySelectorAll('#rolePrivileges input[data-privilege]:checked').length");

        Assert.True(
            ticked > 0,
            "The administrator's editor shows no privileges at all. §4b keeps the rows so this "
            + "screen can display them; an empty read would suggest the role grants nothing.");

        bool save = await Browser.EvaluateAsync<bool>(
            "!!document.getElementById('roleSave') && !document.getElementById('roleSave').hidden");

        Assert.False(save, "Save is offered for a role the server will refuse to change.");

        // And no *set from existing role* either: there is nothing to set.
        bool from = await Browser.EvaluateAsync<bool>(
            "!!document.getElementById('roleFromPick')");

        Assert.False(from, "Set-from-existing is offered for the one role that cannot be set.");

        string[] errors = await PageErrorsAsync();
        Assert.Empty(errors);
    }

    /// <summary>
    /// Set from existing role copies the ticks without saving, and the counts follow.
    /// </summary>
    /// <remarks>
    /// <b>The control the owner marked, and the two halves that make it right.</b> It copies rather
    /// than applies — otherwise *look at what publisher has* becomes *become publisher*, and the
    /// point is to then narrow it. And the counts move with it: a section header still reading
    /// <c>0/11</c> over eleven ticked boxes is the kind of detail that makes a screen untrustworthy.
    /// </remarks>
    [Fact]
    public async Task Set_from_existing_role_copies_the_ticks_and_moves_the_counts()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/roles", token);

        await WaitForAsync(
            "!!document.querySelector('#roleRows tr[data-role=viewer]')",
            "The viewer role is not listed.");

        await ClickAsync("#roleRows tr[data-role='viewer']");

        await WaitForAsync(
            "!!document.getElementById('roleFromPick')",
            "The viewer's editor has no set-from-existing control, so a role can only be built by "
            + "ticking eighteen boxes from nothing.");

        // Viewer grants nothing, so every count starts at zero — which makes the copy visible.
        int before = await Browser.EvaluateAsync<int>(
            "document.querySelectorAll('#rolePrivileges input[data-privilege]:checked').length");

        Assert.Equal(0, before);

        await Browser.EvaluateAsync<object>(
            "(() => { const s = document.getElementById('roleFromPick');"
            + " s.value = 'publisher';"
            + " s.dispatchEvent(new Event('change', { bubbles: true })); })()");

        await WaitForAsync(
            "document.querySelectorAll('#rolePrivileges input[data-privilege]:checked').length > 0",
            "Choosing a role to copy from ticked nothing.");

        int after = await Browser.EvaluateAsync<int>(
            "document.querySelectorAll('#rolePrivileges input[data-privilege]:checked').length");

        Assert.True(after >= 7, $"Copying publisher ticked {after} boxes; it grants at least seven.");

        // <b>The counts followed.</b> Read from the headers rather than recomputed here, because the
        // header is what a reader believes.
        string counts = await Browser.EvaluateAsync<string>(
            "[...document.querySelectorAll('#rolePrivileges .rolesection h4 .val')]"
            + ".map(e => e.textContent.trim()).join(' ')") ?? string.Empty;

        Assert.DoesNotContain(
            "0/",
            counts.Split(' ')[0],
            StringComparison.Ordinal);

        // <b>And nothing was saved.</b> Re-opening the role must show it granting nothing again.
        await OpenAsync("/server/#/roles", token);

        await WaitForAsync(
            "!!document.querySelector('#roleRows tr[data-role=viewer]')",
            "The roles screen did not come back.");

        int stored = await Browser.EvaluateAsync<int>(
            "Number(document.querySelector("
            + "'#roleRows tr[data-role=viewer] .num')?.textContent || '-1')");

        Assert.Equal(
            0,
            stored);

        string[] errors = await PageErrorsAsync();
        Assert.Empty(errors);
    }
}
