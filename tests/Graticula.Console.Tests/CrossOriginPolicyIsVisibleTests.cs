using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// An administrator can read which origins may read this server — ADR-072, owner decision on
/// Q-151, 2026-09-15.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only, and that is the decision rather than an omission.</b> Every origin may read by
/// default, which is what ArcGIS Server has done since 10.1; narrowing it is a deployment's
/// decision, made where the deployment is configured. What the owner asked for is that the policy
/// in force be visible — until this, the only way to find out was to send a request from another
/// origin and read the headers.
/// </para>
/// <para>
/// <b>The sentence is asserted, not just the panel.</b> A metric reading <i>every origin</i> with
/// no surrounding words is the kind of control that exists and is not seen — the failure this
/// suite has caught three times — so the paragraph that says what it means is what this waits for.
/// </para>
/// </remarks>
public sealed class CrossOriginPolicyIsVisibleTests : ConsoleTest
{
    [Fact]
    public async Task Operations_says_which_origins_may_read_and_offers_no_switch()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/operations", token);

        await WaitForAsync(
            "document.getElementById('corsSays')"
            + " && document.getElementById('corsSays').textContent.length > 0",
            "The Operations screen never said which origins may read this server.");

        string says = await Browser.EvaluateAsync<string>(
            "document.getElementById('corsSays').textContent") ?? string.Empty;

        Assert.Contains("may read this server's services", says, StringComparison.Ordinal);

        // What is never allowed is half of the answer: a reader who sees *every origin* and not
        // *never with credentials* reads it as *anybody can act as me*.
        Assert.Contains("never with credentials", says, StringComparison.Ordinal);
        Assert.Contains("Graticula", says, StringComparison.Ordinal);

        // The metric beside the audit carries the policy itself.
        string metrics = await Browser.EvaluateAsync<string>(
            "document.getElementById('routeMetrics').textContent") ?? string.Empty;

        Assert.Contains("Cross-origin reads", metrics, StringComparison.Ordinal);

        // <b>And it is a report, not a control.</b> The panel holds no input, select or button
        // that would set the policy from here.
        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "document.querySelectorAll('#corsSays input, #corsSays select, #corsSays button').length === 0"),
            "The cross-origin paragraph carries a control. Q-151's answer is that this is a "
            + "deployment setting: a console switch would put who may read this server one "
            + "mis-click away.");
    }
}
