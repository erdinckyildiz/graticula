using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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
    /// <summary>
    /// Finds a privilege checkbox by its name, without a nested-quoted CSS selector.
    /// </summary>
    /// <remarks>
    /// <b>A filter rather than <c>[data-privilege='content:create']</c>.</b> The value contains a
    /// colon, so the attribute selector needs quotes, and those quotes then have to survive a C#
    /// string and a JS string — which they did not. The first version of these tests failed with
    /// *missing ) after argument list*, which says nothing about quoting.
    /// </remarks>
    private static string Box(string privilege) =>
        "[...document.querySelectorAll('#rolePrivileges input[data-privilege]')]"
        + $".find(b => b.dataset.privilege === '{privilege}')";

    /// <summary>A privilege and the one it requires, for the dependency test's fixture.</summary>
    private static readonly string[] Pair = ["content:create", "content:publishFeatures"];

    /// <summary>
    /// Choosing a role opens it, and giving it a privilege works from the screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reported by the owner, 2026-08-18:</b> *"varolan bir yetkiye yeni yetki veremiyorum.
    /// viewer seçimi değişmiyor."* — a role could not be selected and therefore nothing could be
    /// given a privilege. Both were one defect: the click handler read <c>t.dataset.role</c> where
    /// <c>t</c> is the cell that was clicked, not the row that carries the name, so choosing a role
    /// did nothing and the editor stayed on whichever role rendered first. Every other row handler
    /// in <c>console.js</c> already used <c>closest</c>.
    /// </para>
    /// <para>
    /// <b>The three earlier tests in this class all passed while this was broken</b>, and that is
    /// the part worth keeping. They asserted the screen's *shape* — sections, counts, disabled
    /// boxes, copying — and never the one act the screen exists for. A suite can be green about
    /// every detail of a control nobody can operate.
    /// </para>
    /// <para>
    /// <b>It edits a role it creates, and removes it.</b> The five built-in roles belong to the
    /// server this suite runs against.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_role_can_be_chosen_and_given_a_privilege_from_the_screen()
    {
        (string token, _) = await SignInAsync();

        const string Probe = "zz_roles_page_probe";

        (int made, string why) = await AdminAsync(
            HttpMethod.Post,
            "/admin/roles",
            JsonSerializer.Serialize(new
            {
                name = Probe,
                description = "Created by RolesPageTests; removed when it finishes.",
                privileges = Array.Empty<string>(),
            }));

        Assert.True(made is 200 or 201, $"Could not create the probe role: {made} {why}");

        try
        {
            await OpenAsync("/server/#/roles", token);

            await WaitForAsync(
                $"!!document.querySelector('#roleRows tr[data-role={Probe}]')",
                "The probe role is not listed.");

            // <b>Clicking a cell, which is what a person does.</b> Clicking the row element itself
            // would have passed against the broken build.
            await ClickAsync($"#roleRows tr[data-role={Probe}] td.name");

            await WaitForAsync(
                "(document.getElementById('roleEditorName')?.textContent || '')"
                + $".includes('{Probe}')",
                "Clicking a role's cell did not open that role. This is the defect the owner "
                + "reported: the editor stays on whichever role rendered first, so every edit is "
                + "aimed at the wrong role or refused.");

            // Tick a privilege that has a prerequisite, and require the screen to tick the
            // prerequisite too — otherwise the save is refused and the operator has to work out
            // which of eleven boxes caused it.
            await Browser.EvaluateAsync<object>(
                $"(() => {{ const b = {Box("content:publishFeatures")};"
                + " b.checked = true;"
                + " b.dispatchEvent(new Event('change', { bubbles: true })); })()");

            await WaitForAsync(
                $"{Box("content:create")}?.checked === true",
                "Ticking content:publishFeatures did not tick content:create, which it requires. "
                + "The server refuses the pair, so the screen has to complete it in front of the "
                + "operator or the refusal is unanswerable.");

            // <b>Read before the save, because the save re-renders — 2026-08-25.</b> This
            // asked the page which boxes were ticked *after* clicking Save, and the comment
            // above already calls it *the set it **would** carry*, which is a
            // before-the-write notion. On a developer machine the read won the race with the
            // re-render; in CI it lost and came back empty, so the assertion compared two
            // privileges against nothing at all. Reading it here asserts the same fact and
            // races nothing.
            //
            // And the set it would carry: both the privilege that was ticked and the one the
            // screen ticked for it.
            string ticked = await Browser.EvaluateAsync<string>(
                "[...document.querySelectorAll('#rolePrivileges input[data-privilege]')]"
                + ".filter(b => b.checked).map(b => b.dataset.privilege).sort().join(',')")
                ?? string.Empty;

            Assert.Equal("content:create,content:publishFeatures", ticked);

            await ClickAsync("#roleSave");

            // <b>The write is asserted, not the row that would have followed it.</b> This harness
            // traps every non-GET and answers `{}` without sending it — *"reads go to the server;
            // writes do not leave the page"* — so a browser test cannot save and must not pretend
            // to. The first version of this test read the API back, found nothing, and the toast
            // said *saved*: the trap had answered 200. Whether the write persists is
            // `RoleDirectoryTests`' subject, against a throwaway schema.
            //
            // What this proves is the half the owner's report was about: the request goes to **the
            // role that was chosen**, which is what a broken row selection breaks.
            string[] writes = await WritesAsync();

            Assert.Contains(
                $"PUT /admin/roles/{Probe}/privileges",
                writes.Select(w => w.Replace("%2F", "/", StringComparison.Ordinal)));

            string[] errors = await PageErrorsAsync();
            Assert.Empty(errors);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/roles/{Probe}");
        }
    }

    /// <summary>
    /// Unticking a prerequisite unticks what needed it.
    /// </summary>
    /// <remarks>
    /// <b>The direction nobody thinks of.</b> Without it the operator removes <c>content:create</c>,
    /// saves, and is told <c>content:publishFeatures</c> requires it — about a box they can still
    /// see ticked.
    /// </remarks>
    [Fact]
    public async Task Unticking_a_prerequisite_unticks_what_required_it()
    {
        (string token, _) = await SignInAsync();

        const string Probe = "zz_roles_page_dep";

        (int made, string why) = await AdminAsync(
            HttpMethod.Post,
            "/admin/roles",
            JsonSerializer.Serialize(new
            {
                name = Probe,
                description = "probe",
                privileges = Pair,
            }));

        Assert.True(made is 200 or 201, $"{made} {why}");

        try
        {
            await OpenAsync("/server/#/roles", token);

            await WaitForAsync(
                $"!!document.querySelector('#roleRows tr[data-role={Probe}]')",
                "The probe role is not listed.");

            await ClickAsync($"#roleRows tr[data-role={Probe}] td.name");

            await WaitForAsync(
                $"{Box("content:publishFeatures")}?.checked === true",
                "The editor did not open the probe role with its privileges ticked.");

            await Browser.EvaluateAsync<object>(
                $"(() => {{ const b = {Box("content:create")};"
                + " b.checked = false;"
                + " b.dispatchEvent(new Event('change', { bubbles: true })); })()");

            await WaitForAsync(
                $"{Box("content:publishFeatures")}?.checked === false",
                "Unticking content:create left content:publishFeatures ticked, so saving would be "
                + "refused for a privilege the operator can still see enabled.");

            string[] errors = await PageErrorsAsync();
            Assert.Empty(errors);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/roles/{Probe}");
        }
    }

    /// <summary>Every privilege on the screen says what it does.</summary>
    /// <remarks>
    /// <para>
    /// <b>[D-100](../../docs/architecture-debt.md): the design review credited this screen's
    /// structure and found the gap is meaning.</b> A role gets a sentence; the eighteen
    /// privileges under it were bare identifiers with dependency notes. An administrator deciding
    /// who may do what was reading a list of names.
    /// </para>
    /// <para>
    /// <b>Asserted on the screen and not only on the endpoint.</b> The sentence reaching
    /// `/admin/roles` and never being rendered is the same gap with an extra field in it, and it
    /// is the more likely of the two failures: the endpoint's half is covered by a unit test that
    /// cannot see a template.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Every_privilege_on_the_screen_says_what_it_does()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/roles", token);

        await WaitForAsync(
            "document.querySelectorAll('#rolePrivileges input[data-privilege]').length >= 18",
            "The roles screen offered fewer than eighteen privileges, so there is nothing to read.");

        // Each row's own sentence, read beside its own identifier, so a failure names the
        // privilege rather than reporting a count.
        string bare = await Browser.EvaluateAsync<string>(
            """
            [...document.querySelectorAll('#rolePrivileges .roleprivilege')]
              .filter(l => (l.querySelector('.privilegewhat')?.textContent || '').trim().length < 30)
              .map(l => l.querySelector('[data-privilege]')?.dataset.privilege || '?')
              .join(', ')
            """) ?? string.Empty;

        Assert.True(
            bare.Length == 0,
            $"These privileges are offered with no explanation of what they do: {bare}. The "
            + "screen an administrator reads to decide who can do what has to say what anything "
            + "does.");

        string[] errors = await PageErrorsAsync();
        Assert.Empty(errors);
    }

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
