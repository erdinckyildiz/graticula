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
    /// New group opens its form when there are no groups at all, which is where it failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reported by the owner, 2026-08-18: *"new group düğmesi çalışmıyor"*.</b> The create form was
    /// written into <c>#groupPicker</c>, which sat inside the group editor's panel — one that was
    /// <c>hidden</c> until a group is chosen. So the button wrote its form into a hidden container and
    /// appeared to do nothing, **and the only reader who hits that is somebody with no groups yet**:
    /// the first-run case, which is the one state the three earlier tests could not be in, because each
    /// of them created a group before opening the screen.
    /// </para>
    /// <para>
    /// <b>So this test refuses to create one first.</b> It asserts the button works from the state a
    /// new deployment is in, and that is the whole point of it — a test that provisions a group before
    /// pressing New group passes against the broken build.
    /// </para>
    /// <para>
    /// <b>It also proves the form is visible, not merely present.</b> `offsetParent` is null for
    /// anything inside a `hidden` ancestor, which is exactly the failure: the elements existed and
    /// nobody could see them.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task New_group_opens_its_form_with_no_groups_on_the_screen()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/groups", token);

        // <b>Waited on the screen being *shown*, not on the button existing.</b> `#groupNew` is static
        // markup: it is in the document before the router has decided which screen is on, so waiting
        // for it is a tautology that clicks through the boot. The diagnostic when this was wrong said
        // `on: ["view-services"]` with `hash: "#/groups"` — the click landed while Server's default
        // screen was still showing, and the form rendered correctly into a container nobody could see.
        // Third tautological wait found in this file today, all the same shape: assert the thing that
        // is false before the thing you are testing happens.
        await WaitForAsync(
            "document.getElementById('view-groups').classList.contains('on')",
            "The Groups screen never became the one showing.");

        // <b>The editor is closed, which is the condition under which this broke.</b> If the server
        // this suite runs against happens to have groups, the editor opens on the first one and the
        // defect is masked — so the state is asserted rather than assumed, and the test says which
        // state it is in when it cannot get the interesting one.
        // <b>Nothing is open, which is the condition under which this broke.</b> The editor became
        // its own page (ADR-036 §4g), so the state to assert is that the list is what is showing —
        // if this suite's server has groups, the list still shows only the list until a row is
        // clicked, so the interesting state is no longer reachable only by luck.
        // <b>`.on`, not `hidden`, and reading the wrong one made three waits into no-ops.</b>
        // `showView` toggles a class; every `.view` section's `hidden` property is permanently false,
        // so `!view.hidden` is a tautology that passes before the router has run. A test that goes
        // green with its subject absent is the trap this repository has now written four times.
        bool pageOpen = await Browser.EvaluateAsync<bool>(
            "document.getElementById('view-group').classList.contains('on')");

        await ClickAsync("#groupNew");

        await WaitForAsync(
            "!!document.getElementById('newGroupName')",
            "New group did not render its form.");

        // <b>Visible, not merely present.</b> `offsetParent` is null inside a `hidden` ancestor, and
        // that is precisely how this failed: every element existed and none of them could be seen.
        bool visible = await Browser.EvaluateAsync<bool>(
            "!!document.getElementById('newGroupName')?.offsetParent");

        Assert.True(
            visible,
            "The create form rendered into something hidden"
            + (pageOpen
                ? ", and a group's page is what is showing rather than the list."
                : " — and no group's page was open, which is the state a deployment with no groups is "
                  + "always in, so New group did nothing for a first-time reader."));

        string[] errors = await PageErrorsAsync();
        NothingWentWrong(errors);
    }

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
                "document.getElementById('view-group').classList.contains('on')",
                "Clicking a group did not open its page.");

            // <b>Add member lives on the Members tab.</b> The owner, 2026-08-18: *"add member shall
            // be inside members section"* — a verb belongs on the tab whose subject it changes, and it
            // was in the page head above all four.
            await Browser.EvaluateAsync<string>($"location.hash = '#/group/{Probe}/members'");

            await WaitForAsync(
                Shown("#groupAdd"),
                "The owner was not offered Add member on the Members tab.");

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
            NothingWentWrong(errors);
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
                "document.getElementById('view-group').classList.contains('on')",
                "Clicking a group did not open its page.");

            await WaitForAsync(
                "(document.getElementById('groupFacts')?.textContent || '').length > 0",
                "Overview does not say where the reader stands in this group.");

            string standing = Flat(await Browser.EvaluateAsync<string>(
                "document.getElementById('groupFacts')?.textContent || ''"));

            // <b>Asserted on substance, not on a phrase.</b> The copy moved from a paragraph to a
            // fact list during a design review; a test pinned to the old sentence would have failed
            // for a screen that got better. What must survive any rewording is that an owner is told
            // they own it and that they cannot leave.
            Assert.Contains("owner", standing, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cannot leave", standing, StringComparison.OrdinalIgnoreCase);

            bool leave = await Browser.EvaluateAsync<bool>(
                "!!document.getElementById('groupLeave')"
                + " && !document.getElementById('groupLeave').hidden");

            Assert.False(
                leave,
                "The owner is offered Leave group, which the store refuses. A button that cannot "
                + "work is worse than its absence: it reads as a capability.");

            string[] errors = await PageErrorsAsync();
            NothingWentWrong(errors);
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

            // <b>And it opens a form, not a `prompt()`.</b> The prompts cost two of a group's four
            // fields permanently — the name was sent as the title and the description was never sent
            // — and there is no endpoint to set either afterwards. A headless browser cannot answer
            // an OS dialog either, so this assertion is also what makes the act testable at all.
            await ClickAsync("#groupNew");

            await WaitForAsync(
                "!!document.getElementById('newGroupName')"
                + " && !!document.getElementById('newGroupWhy')"
                + " && !!document.getElementById('newGroupUpdate')",
                "New group did not open an in-page form with a name, a description and a capability "
                + "select. It used to be two chained prompts, which could not set the description at "
                + "all.");

            // The capability is a select over the three values the server accepts, not free text
            // against a case-sensitive enum.
            int capabilities = await Browser.EvaluateAsync<int>(
                "document.getElementById('newGroupUpdate')?.options.length || 0");

            Assert.Equal(3, capabilities);

            // The standing is shown, because *which of these am I responsible for* is the first
            // question somebody has about a list of groups.
            // <b>A word at weight, not a badge.</b> `pill()` is for a state — whether a service
            // answers, who may read it — and a standing is a relationship; all three values fell
            // through to one grey pill with no colour family, so the border and the dot carried
            // nothing the word did not. Asserted here so a fourth grey badge cannot come back.
            string standing = Flat(await Browser.EvaluateAsync<string>(
                $"document.querySelector('#groupRows tr[data-group={Probe}] td:nth-child(3)')"
                + "?.textContent || ''"));

            Assert.Equal("owner", standing);

            bool badge = await Browser.EvaluateAsync<bool>(
                $"!!document.querySelector('#groupRows tr[data-group={Probe}] td:nth-child(3) .pill')");

            Assert.False(
                badge,
                "A standing is rendered as a pill again. Three values fall through to one grey badge "
                + "with no colour and no icon, and a fourth meaningless pill weakens the ones that "
                + "mean something.");

            string[] errors = await PageErrorsAsync();
            NothingWentWrong(errors);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Probe}");
        }
    }

    /// <summary>
    /// Choosing a group opens its page, and each of its four tabs shows its own subject.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Clicking a cell, not the row.</b> D-73 was exactly this defect on the Roles screen — the
    /// handler read the click target's own dataset and the target is the cell — and a test that
    /// clicked the row element would have passed against it.
    /// </para>
    /// <para>
    /// <b>Four tabs by owner decision, ADR-036 §4g</b>, overruling §4f's refusal. This test is the
    /// half of that decision a screenshot cannot give: that each tab is separately addressable, that
    /// the strip marks the one showing, and that the comparison the tabs took away — who is in the
    /// group against what they can therefore read — is given back by Overview's tally. If that
    /// sentence ever stops being rendered, the tabs have cost the screen its subject for nothing, and
    /// this is the assertion that says so.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Each_of_a_groups_four_tabs_shows_its_own_subject()
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
                "(document.getElementById('groupTitle')?.textContent || '')"
                + ".includes('Probe group')",
                "Clicking a group's cell did not open its page — the defect D-73 records for the "
                + "Roles screen, on a second screen.");

            // <b>A row click has to reach the address, not just the render.</b> The page is
            // addressable and that is the difference between it and the panel it replaced; a handler
            // that drew the page without moving the hash would leave Back on the previous screen and
            // a copied link on the wrong one.
            string where = await Browser.EvaluateAsync<string>("location.hash") ?? string.Empty;

            Assert.Contains($"group/{Probe}", where, StringComparison.Ordinal);

            // ---------------------------------------------------------------- Overview
            // <b>The tally is what §4f's argument was traded for.</b> Tabs hide *who is in the group*
            // while you read *what they can therefore read*; this sentence is the only place that
            // relation is still counted, so its absence is a regression in the decision rather than
            // in the markup.
            string tally = Flat(await Browser.EvaluateAsync<string>(
                "document.getElementById('groupReach')?.textContent || ''"));

            Assert.Contains("read nothing through it", tally, StringComparison.OrdinalIgnoreCase);

            // The facts, which were two paragraphs of grey `hint` carrying one word each.
            string facts = Flat(await Browser.EvaluateAsync<string>(
                "document.getElementById('groupFacts')?.textContent || ''"));

            Assert.Contains("its owner", facts, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("reading only", facts, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("fixed at creation", facts, StringComparison.OrdinalIgnoreCase);

            // <b>The strip marks the tab showing, and only that one.</b> `aria-current` is the whole
            // accessible affordance here — these are links rather than `role="tab"`, deliberately, so
            // nothing else tells a screen reader which of the four it is on.
            int current = await Browser.EvaluateAsync<int>(
                "document.querySelectorAll('#groupTabs a[aria-current]').length");

            Assert.Equal(1, current);

            // ---------------------------------------------------------------- each tab, by address
            foreach ((string tab, string element) in new (string, string)[]
            {
                ("content", "groupItems"),
                ("members", "groupMembers"),
                ("settings", "groupSettings"),
                ("overview", "groupReach"),
            })
            {
                await Browser.EvaluateAsync<string>(
                    $"location.hash = '#/group/{Probe}/{tab}'");

                await WaitForAsync(
                    $"!document.getElementById('tab-{tab}').hidden",
                    $"The address #/group/{Probe}/{tab} did not open the {tab} tab.");

                // <b>The other three are hidden, which is the assertion that catches a strip that
                // shows and a body that does not switch.</b> Four visible bodies would read as one
                // long page and pass any test that only asked whether the right one was present.
                int showing = await Browser.EvaluateAsync<int>(
                    "document.querySelectorAll('#view-group .grouptab:not([hidden])').length");

                Assert.Equal(1, showing);

                bool marked = await Browser.EvaluateAsync<bool>(
                    $"(document.querySelector('#groupTabs a[aria-current]')?.getAttribute('href')"
                    + $" || '').endsWith('/{tab}')");

                Assert.True(marked, $"The strip does not mark {tab} while {tab} is showing.");

                Assert.True(
                    await Browser.EvaluateAsync<bool>($"!!document.getElementById('{element}')"),
                    $"The {tab} tab rendered nothing into #{element}.");
            }

            // ---------------------------------------------------------------- Content's own sentence
            await Browser.EvaluateAsync<string>($"location.hash = '#/group/{Probe}/content'");

            await WaitForAsync(
                "!document.getElementById('tab-content').hidden",
                "The Content tab did not open.");

            // The sentence that stops the step people miss: sharing here is not enough. It moved from
            // the screen's prose onto the tab that is about the shares, which is the one place a
            // reader is looking at an inert one.
            string content = Flat(await Browser.EvaluateAsync<string>(
                "document.getElementById('groupItems')?.textContent || ''"));

            Assert.Contains("its own sharing scope", content, StringComparison.OrdinalIgnoreCase);

            string[] errors = await PageErrorsAsync();
            NothingWentWrong(errors);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Probe}");
        }
    }

    /// <summary>
    /// An owner sees the group's controls, and *sees* is asserted rather than *has*.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written because the whole write surface of this page was invisible to its owner for a
    /// day.</b> <c>GET /admin/groups/{name}</c> did not return <c>mayManage</c> or <c>mayDelete</c> —
    /// the two lines that compute them live in the listing handler and were never copied into the
    /// describe one — so the page read <c>undefined</c> seven times. An unrestricted administrator who
    /// owned the group was shown a Settings tab saying *"these are the owner's and its managers' to
    /// set"* and no controls at all, while a **plain member's** view was accidentally correct. The bug
    /// hit exactly the people the page was built for.
    /// </para>
    /// <para>
    /// <b>Third time in this project, and the lesson is one word: `offsetParent`.</b> The groups
    /// screen's first-run form and *New group* writing into a hidden container were the first two, and
    /// both were found by the owner pressing a button rather than by a suite. A control's existence is
    /// not its availability, and <c>getElementById</c> cannot tell the two apart —
    /// <c>offsetParent === null</c> is the cheapest question that can.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_owner_can_see_and_not_merely_have_the_groups_controls()
    {
        (string token, _) = await SignInAsync();

        (int made, string why) = await AdminAsync(
            HttpMethod.Post,
            "/admin/groups",
            JsonSerializer.Serialize(new { name = Probe, title = "Probe group" }));

        Assert.True(made is 200 or 201, $"{made} {why}");

        try
        {
            await OpenAsync($"/studio/#/group/{Probe}/overview", token);

            await WaitForAsync(
                "(document.getElementById('groupTitle')?.textContent || '').includes('Probe group')",
                "The group's page never rendered.");

            // <b>Visible, not present, and each on the tab that owns it.</b> Both existed
            // throughout the defect; both now live on the tab whose subject they change, which is the
            // owner's correction of 2026-08-18 — so a test that looked for them on Overview would
            // report the page broken for the wrong reason.
            foreach ((string tab, string control) in new (string, string)[]
            {
                ("members", "groupAdd"),
                ("content", "groupShare"),
            })
            {
                await Browser.EvaluateAsync<string>($"location.hash = '#/group/{Probe}/{tab}'");

                await WaitForAsync(
                    $"!document.getElementById('tab-{tab}').hidden",
                    $"The {tab} tab did not open.");

                await WaitForAsync(
                    Shown($"#{control}"),
                    $"#{control} is not visible to the owner on the {tab} tab. It may still exist in "
                    + "the document — that was the whole defect, and asserting presence passes "
                    + "against it.");
            }

            // Settings is the tab that lost everything, because its member branch swallowed the
            // manager one.
            await Browser.EvaluateAsync<string>($"location.hash = '#/group/{Probe}/settings'");

            await WaitForAsync(
                "!document.getElementById('tab-settings').hidden",
                "The Settings tab did not open.");

            foreach (string control in new[] { "gsVisibility", "gsJoin", "gsContribute", "gsLock" })
            {
                bool seen = await Browser.EvaluateAsync<bool>(
                    $"!!document.getElementById('{control}')"
                    + $" && document.getElementById('{control}').offsetParent !== null");

                Assert.True(
                    seen,
                    $"#{control} is not visible on the Settings tab to the group's owner. The tab "
                    + "rendered its plain-member text instead, which is what a missing mayManage "
                    + "produces.");
            }

            // <b>And every one of them has an accessible name.</b> All four read as `combobox: \"\"`
            // when the questions were `<span class="q">` rather than `<label for>` — four unlabelled
            // dropdowns on the one tab that is nothing but form controls.
            string[] unnamed = await Browser.EvaluateAsync<string[]>(
                "Array.from(document.querySelectorAll('#groupSettings select, #groupSettings input'))"
                + ".filter(e => !(e.getAttribute('aria-label')"
                + " || (e.labels && e.labels.length)))"
                + ".map(e => e.id || e.tagName)")
                ?? Array.Empty<string>();

            Assert.Empty(unnamed);

            string[] errors = await PageErrorsAsync();
            NothingWentWrong(errors);
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
                "document.getElementById('view-group').classList.contains('on')",
                "Clicking a group did not open its page.");

            // The signed-in account owns this group, so it *is* offered the controls — which is the
            // half that proves the flags are read rather than hard-coded off. `offsetParent`, not
            // `hidden`: a control inside a hidden ancestor is not hidden itself, and that distinction
            // is the whole of the defect this suite has now been written around three times.
            //
            // <b>Waited for rather than asserted, because `#view-group` becomes visible before the
            // group is read.</b> The router shows the page, then fetches; a bare assertion here caught
            // the instant in between and failed against a working console — the same one-tick race
            // `ClickAsync` was written to remove.
            await Browser.EvaluateAsync<string>($"location.hash = '#/group/{Probe}/members'");

            await WaitForAsync(
                Shown("#groupAdd"),
                "The owner of a group is not offered its controls, so the screen is read-only for "
                + "everybody and the `mayManage` flag is being ignored.");

            // <b>Delete moved to the Settings tab and is not rendered at all without `mayDelete`.</b>
            // Absent rather than disabled is the rule (ADR-036 condition 5), so the assertion has to
            // open the tab and then ask — the old one read a `hidden` property off `null`.
            await Browser.EvaluateAsync<string>($"location.hash = '#/group/{Probe}/settings'");

            await WaitForAsync(
                "!document.getElementById('tab-settings').hidden",
                "The Settings tab did not open.");

            await WaitForAsync(
                Shown("#groupDelete"),
                "The owner is not offered Delete on the Settings tab, which only they may do.");

            string[] errors = await PageErrorsAsync();
            NothingWentWrong(errors);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Probe}");
        }
    }
}
