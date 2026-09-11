using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// That what the console says in a red toast reaches a timed-out wait's report.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-259](../../docs/architecture-debt.md).</b> A screen's loader runs inside `section()`,
/// which catches whatever it throws and shows a toast. So a loader that throws is neither an
/// `error` nor an `unhandledrejection`, and for a day the Publish screen failed in CI with a
/// report that said *the page recorded no errors of its own* — while the screen's one fetch had
/// died on `ERR_CERT_VERIFIER_CHANGED` and the toast saying so vanished seven seconds later.
/// </para>
/// <para>
/// <b>Asserted rather than trusted, for the reason the missing-file test gives.</b> An observer
/// that watches the wrong element, or a report that computes the line and forgets to print it —
/// which the first draft of this did — compiles, runs and passes every other test. Only a toast
/// that is shown on purpose tells them apart.
/// </para>
/// </remarks>
public sealed class HarnessHearsARedToastTests : ConsoleTest
{
    [Fact]
    public async Task A_red_toast_is_in_the_report_and_a_green_one_is_not()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/services", token);

        await Browser.EvaluateAsync<bool>(
            """
            (() => {
              toast("probe d-259: the green one, which is the page reporting success", true);
              toast("probe d-259: a screen's loader threw", false);
              return true;
            })()
            """);

        Exception timedOut = await Assert.ThrowsAnyAsync<Exception>(() => WaitForAsync(
            "false",
            "This wait is meant to run out, so that its report can be read."));

        Assert.Contains(
            "red toasts: probe d-259: a screen's loader threw",
            timedOut.Message,
            StringComparison.Ordinal);

        Assert.DoesNotContain("the green one", timedOut.Message, StringComparison.Ordinal);
    }
}
