using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// That a file which never arrives is recorded as that, and not as its consequence.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-176](../../docs/architecture-debt.md).</b> A failed `&lt;script src&gt;` fires
/// `error` on the element and **does not bubble**, so the bubbling listener this harness
/// had could only ever see what happened next. On 2026-08-26 a CI run reported
/// `Uncaught ReferenceError: OSM_TILES is not defined` thrown by `view.js`, when what had
/// happened was that `ground.js` — which defines it — never loaded. One line of cause,
/// reported as one line of consequence, on a defect ([D-173](../../docs/architecture-debt.md))
/// whose whole difficulty is that it wears a different name every time.
/// </para>
/// <para>
/// <b>Asserted rather than trusted, because the mistake is invisible.</b> A capture-phase
/// listener and a bubbling one are one argument apart and both compile, both run, and both
/// look right in review; the only thing that tells them apart is a resource that fails. So
/// this makes one fail on purpose.
/// </para>
/// </remarks>
public sealed class HarnessSeesAMissingFileTests : ConsoleTest
{
    [Fact]
    public async Task A_script_that_never_arrives_is_recorded_as_that()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/services", token);

        // <b>Appended rather than served, so no file is renamed and no page is edited.</b>
        // The address is under the surface the console is served from, which makes this a
        // real 404 from the real static file middleware rather than a cross-origin failure
        // the browser reports differently.
        await Browser.EvaluateAsync<bool>(
            """
            (() => {
              const s = document.createElement('script');
              s.src = '/server/no-such-file-d176.js';
              document.head.appendChild(s);
              return true;
            })()
            """);

        await WaitForAsync(
            "(window.__pageErrors || []).some(e => e.indexOf('never arrived') >= 0)",
            "A script pointed at a file this server does not have was added to the page, and "
            + "nothing recorded that it failed to arrive. That is D-176: the listener is "
            + "bubbling rather than capturing, so a resource failure is invisible to it and "
            + "only its consequence is ever reported.");

        string[] recorded = await PageErrorsAsync();

        Assert.Contains(
            recorded,
            e => e.Contains("no-such-file-d176.js", StringComparison.Ordinal));

        // The console's own scripts loaded; this must not have swept them up as failures.
        Assert.DoesNotContain(
            recorded,
            e => e.Contains("never arrived", StringComparison.Ordinal)
                && e.Contains("console.js", StringComparison.Ordinal));
    }
}
