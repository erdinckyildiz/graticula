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
