using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Who is shown the Server surface, and who is not shown that there is one.
/// </summary>
public sealed class SurfaceTests : ConsoleTest
{
    /// <summary>
    /// Server's own action opens the drawer, and the drawer holds a form that works.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[D-90](../../docs/architecture-debt.md): a page action with no test.</b> ADR-034 §5j
    /// split one four-job drawer into a *New item* dialog and Server's own *New service* action.
    /// The endpoints kept their tests and the item route is walked end to end; nothing pressed
    /// this button. The row's own words are that this is how D-83 and D-87 both shipped — the
    /// capability complete and the console unable to reach it.
    /// </para>
    /// <para>
    /// <b>The form is filled and submitted, not only looked at.</b> A test that asserts the
    /// drawer opens proves the button is wired and nothing else; what broke twice this month was
    /// the wiring between a form and its endpoint.
    /// </para>
    /// <para>
    /// <b>What the page tried to write, not what the server stored.</b> This suite answers every
    /// non-GET from inside the page itself, so nothing is created on the operator's server to
    /// prove a form works — and the assertion is the stronger one anyway: the request the
    /// form produced. `POST /admin/featureservices` has its own tests; that this button reaches
    /// it is what nobody was checking.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Servers_own_action_opens_the_drawer_and_its_form_creates_a_service()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/services", token);

        await WaitForAsync(
            "document.getElementById('newService') !== null",
            "Server's services screen offers no New service action, so ADR-034 §5j's action "
            + "has nowhere to be pressed.");

        await ClickAsync("#newService");

        await WaitForAsync(
            "document.getElementById('drawer').classList.contains('on')",
            "Pressing New service opened nothing. The endpoints behind it have their own tests; "
            + "this is the page action D-90 says nobody presses.");

        await WaitForAsync(
            "document.getElementById('cName') !== null",
            "The drawer opened without the service form in it.");

        await FilterAsync("cName", "zz_d90_probe");

        await ClickAsync("#svcForm button[type=submit]");

        await WaitForAsync(
            "(window.__writes || []).some(w => w.startsWith('POST ') "
            + "&& w.includes('/admin/featureservices'))",
            "Submitting the service form sent nothing to /admin/featureservices. The capability "
            + "is complete and the console cannot reach it, which is how D-83 and D-87 both "
            + "shipped.");

        string[] errors = await PageErrorsAsync();
        Assert.Empty(errors);
    }

    /// <summary>
    /// Closing the drawer gives focus back to what opened it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[D-93](../../docs/architecture-debt.md), and the row was half stale when it was
    /// read.</b> It says opening the drawer leaves focus on the trigger; opening it moves focus
    /// to the first field, and has since the review that wrote the row. What was still true is
    /// the other end: closing it made the drawer `inert` while focus was inside, so the browser
    /// dropped focus to `<body>` and a keyboard operator restarted from the top of the page.
    /// </para>
    /// <para>
    /// <b>The same defect this console already fixed once</b>, on the data-source edit form,
    /// where clearing the panel deleted the element that had focus. Asserted the same way.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Closing_the_drawer_puts_focus_back_where_it_came_from()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/services", token);

        await WaitForAsync(
            "document.getElementById('newService') !== null",
            "Server's services screen offers no New service action.");

        await ClickAsync("#newService");

        // Opening moves focus into the drawer, which is the half of D-93 already repaired.
        await WaitForAsync(
            "document.activeElement?.closest('#drawer') !== null",
            "Opening the drawer left focus outside it, so the first Tab goes to whatever is next "
            + "on the page behind.");

        await ClickAsync("#drawerClose");

        await WaitForAsync(
            """
            (() => {
              const now = document.activeElement;
              return now !== null
                  && now !== document.body
                  && now.closest('#drawer') === null
                  && (now.id === 'app' || now.offsetParent !== null);
            })()
            """,
            "After closing the drawer, focus is on `<body>` — the drawer was made inert while it "
            + "still held focus, so a keyboard reader starts again from the top of the page.");

        string[] errors = await PageErrorsAsync();
        Assert.Empty(errors);
    }

    /// <summary>
    /// A reader without <c>admin:manageServer</c> is put in Studio and shown no
    /// switch out of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is ADR-034 condition 1, and it is the condition's own words:</b> a
    /// reader who does not hold <c>admin:manageServer</c> must not be shown the
    /// Server surface. The owner asked for it directly — <em>"admin olmayan
    /// kullanıcılar şuradaki server studio ayrımını görmeyecek bile"</em>.
    /// </para>
    /// <para>
    /// <b>D-59's third defect is why the assertion is about rendering rather than
    /// about the attribute.</b> The switch was hidden by setting
    /// <c>surfaces.hidden = true</c>, and <c>#surfaces { display: flex }</c> won:
    /// the attribute was set, the element was visible, and a reading of the code
    /// said it was hidden. So this asks the browser what it painted —
    /// <c>offsetParent</c> and the computed <c>display</c> — which is the only
    /// question that could have caught it.
    /// </para>
    /// <para>
    /// <b>The refusal is asserted too.</b> Entering <c>/server/</c> without the
    /// privilege must leave the reader in Studio rather than on an empty Server
    /// screen full of 403s. That is the same address in the path that ADR-034 §5a
    /// chose, so a redirect is observable: the path changes.
    /// </para>
    /// <para>
    /// <b>The privilege is removed from the server's own answer, not invented.</b>
    /// See <see cref="ConsoleTest"/>: there is no <c>DELETE /admin/members</c>, so
    /// a test that created a publisher would leave one behind on every run.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Without_admin_manageServer_there_is_no_Server_surface_to_see()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/services", token, cookie: null, "admin:manageServer");

        await WaitForAsync(
            "location.pathname.startsWith('/studio')",
            "A reader without admin:manageServer was left in Server. ADR-034 §5b has them put in "
            + "Studio with a sentence, not shown a screen where every request is refused.");

        await WaitForAsync(
            // <b>The existence check is not defensive noise.</b> A wait whose expression
            // throws while the element is still missing is a race, not a wait — and this one
            // threw the day `session.js` was added, because a script that blocks in the head
            // widens the window in which the document has a head and no body. Guarding with
            // `&&` keeps the wait waiting; a fallback to `document.body` would have made it
            // pass while the element did not exist, which is worse than the failure.
                        "!!document.getElementById('app')"
            + " && getComputedStyle(document.getElementById('app')).display !== 'none'",
            "Studio did not open for a reader who is entitled to it.");

        await DrawnAsync();

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "document.getElementById('surfaces').offsetParent === null"),
            "The Server/Studio switch was painted for a reader who cannot enter Server. Setting "
            + "the hidden attribute is not enough on its own: #surfaces carries a display of its "
            + "own, and an author display beats [hidden] (D-46 #9).");

        Assert.Equal(
            "none",
            await Browser.EvaluateAsync<string>(
                "getComputedStyle(document.getElementById('surfaces')).display"));
    }

    /// <summary>
    /// An administrator is shown the switch, and both surfaces in it.
    /// </summary>
    /// <remarks>
    /// The pair. Hiding the switch from everybody satisfies the test above and
    /// removes the only way to reach Server, which is a worse defect than the one
    /// it replaces.
    /// </remarks>
    [Fact]
    public async Task An_administrator_is_shown_both_surfaces()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/services", token);

        await WaitForAsync(
            "!!document.getElementById('app')"
            + " && getComputedStyle(document.getElementById('app')).display !== 'none'",
            "The console did not open for an administrator.");

        await DrawnAsync();

        Assert.False(
            await Browser.EvaluateAsync<bool>(
                "document.getElementById('surfaces').offsetParent === null"),
            "An administrator was shown no way to reach Studio.");

        Assert.Equal(
            2,
            await Browser.EvaluateAsync<int>(
                "document.querySelectorAll('#surfaces a').length"));
    }

    /// <summary>
    /// Waits for the header to have been drawn, which is not when the body appears.
    /// </summary>
    /// <remarks>
    /// <b>`#surfaces` starts hidden in the markup and `drawSurfaces` decides.</b>
    /// `start()` reveals `#app` first, so a test that waited for the body read the
    /// switch before anybody had drawn it, and asserted about the default rather
    /// than about the decision — it failed one run in six. `drawSurfaces` stamps
    /// the surface on the root element in the same synchronous block that sets the
    /// switch's visibility, which makes that stamp the signal it has run, and it is
    /// independent of what these tests assert.
    /// </remarks>
    private Task DrawnAsync() =>
        WaitForAsync(
            "document.documentElement.dataset.surface",
            "The console never recorded which surface it had drawn, so drawSurfaces did not run "
            + "within ten seconds.");
}
