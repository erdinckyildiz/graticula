using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The sweep that removes the containers a publish-and-unpublish cycle leaves.
/// </summary>
public sealed class EmptyServiceSweepTests : ConsoleTest
{
    /// <summary>
    /// Remove is refused until the operator has looked, and enabled only if there is
    /// something to remove.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the whole safety of the pair, and it is a UI fact.</b> D-54 refused
    /// automatic removal because nothing records which services were created
    /// deliberately, so the judgement belongs to a person — and a person cannot judge
    /// an estate they have not looked at. The button starts disabled, and stays
    /// disabled when the list comes back empty.
    /// </para>
    /// <para>
    /// <b>The second half matters more than it looks.</b> A destructive verb that is
    /// pressable and does nothing is how people learn a button is harmless, and then
    /// press it on the day it is not.
    /// </para>
    /// <para>
    /// <b>The write never leaves the page</b>, per this suite's rule: what is under
    /// test is the gate, not the deletion. The deletion is measured in
    /// `PostgresAdminCatalogTests.The_sweep_removes_empty_services_and_keeps_everything_that_holds_something`
    /// and against the running server in D-54.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Remove_is_locked_until_the_estate_has_been_read()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/operations", token);

        await WaitForAsync(
            "document.getElementById('emptySweep')",
            "The Operations screen has no empty-services panel.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "document.getElementById('emptySweep').disabled"),
            "Remove was pressable before anybody had looked at what it would remove.");

        await ClickAsync("#emptyRead");

        await WaitForAsync(
            "!document.getElementById('emptyWhen').textContent.includes('Reading')"
            + " && document.getElementById('emptyWhen').textContent.length > 0",
            "The panel never reported what it found.");

        string when = await Browser.EvaluateAsync<string>(
            "document.getElementById('emptyWhen').textContent") ?? string.Empty;

        bool anything = !when.Contains("nothing", StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            anything,
            !await Browser.EvaluateAsync<bool>(
                "document.getElementById('emptySweep').disabled"));

        // Whichever way round it came out, the table says which — a count with no names
        // is the thing this panel exists not to be.
        Assert.NotEmpty(
            await Browser.EvaluateAsync<string>(
                "document.getElementById('emptyRows').textContent") ?? string.Empty);
    }
}
