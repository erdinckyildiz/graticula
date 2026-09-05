using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The Publish screen composes a service and sends it as one act.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-057](../../docs/adr/ADR-057-composing-and-publishing-a-service.md), and the screen
/// it replaces is still next door.</b> Server's *New service* drawer asks for a container, then
/// a group, then a layer index nobody can find — a design review on 2026-09-06 called it the
/// API rendered as a form. This screen asks for none of that: tables go into a tree, the tree
/// is the service, and one request writes it.
/// </para>
/// <para>
/// <b>What this harness can and cannot see.</b> Every non-GET is trapped and answered with
/// <c>{}</c>, so the publish itself is covered over real HTTP by
/// <c>PublishCompositionConformanceTests</c>. What is under test here is the half that is the
/// screen: that a table can be got into the composition at all, that the summary says what will
/// exist, and that pressing Publish sends one request to the composition endpoint rather than
/// the three the old drawer needed.
/// </para>
/// </remarks>
public sealed class PublishScreenTests : ConsoleTest
{
    /// <summary>
    /// A table becomes a layer, the summary names it, and Publish sends one request.
    /// </summary>
    [Fact]
    public async Task A_table_becomes_a_layer_and_publishing_sends_one_request()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/publish", token);

        await WaitForAsync(
            "(() => { const e = document.getElementById('pubDbTree'); "
            + "return !!e && e.offsetParent !== null; })()",
            "The Publish screen did not draw its Databases pane. This console has shipped a "
            + "control that existed and rendered nowhere three times; that is what offsetParent "
            + "is here for.");

        // <b>The datastore, which every fixture has.</b> Opening it probes the source, which is
        // a real read of somebody's database — so the wait is on the answer rather than a sleep.
        await WaitForAsync(
            "document.querySelectorAll('#pubDbTree [data-pubdb]').length > 0",
            "No registered database is listed, so there is nothing to compose from.");

        await ClickAsync("#pubDbTree [data-pubdb]");

        await WaitForAsync(
            "document.querySelectorAll('#pubDbTree [data-pubtable]').length > 0",
            "Opening a database listed no tables. Either the probe failed or the tree does not "
            + "draw what it read.");

        // <b>Clicked, not dragged.</b> A synthetic click is what this harness can send, and the
        // screen accepts both on purpose — the same lesson the connection dialog's combo
        // learned on 2026-09-05, where listening only for what a real mouse sends made a
        // control this suite could not press.
        string chosen = await Browser.EvaluateAsync<string>(
            "document.querySelector('#pubDbTree [data-pubtable][draggable=true]')"
            + "?.getAttribute('data-pubtable') || ''") ?? string.Empty;

        Assert.False(
            string.IsNullOrWhiteSpace(chosen),
            "Every table in this database is either unpublishable or already served, so there "
            + "is nothing this test can compose with. A table is one layer on this server "
            + "(ADR-057 §5i), so the fixture needs one that is free.");

        await ClickAsync($"#pubDbTree [data-pubtable='{chosen}']");

        await WaitForAsync(
            "document.querySelectorAll('#pubTree [data-pubnode]').length === 1",
            "The table did not become a layer in the composition.");

        // <b>The summary is the middle pane, and it is not a map.</b> Nothing on this server
        // turns an unpublished composition into a picture, so what is drawn is the service that
        // will exist — which is also the thing this test can assert.
        await WaitForAsync(
            "(document.getElementById('pubWhat')?.innerText || '').includes('index 0')",
            "The summary does not say what will exist. It is the only thing on this screen that "
            + "reports the composition, so an empty one is a screen that shows nothing.");

        Assert.False(
            await Browser.EvaluateAsync<bool>("document.getElementById('pubOpen').disabled"),
            "Publish is still disabled with a layer in the composition.");

        await ClickAsync("#pubOpen");

        await WaitForAsync(
            "(() => { const e = document.getElementById('pbName'); "
            + "return !!e && e.offsetParent !== null; })()",
            "The Publish dialog did not open.");

        await Browser.EvaluateAsync<bool>(
            """(document.getElementById("pbName").value = "ZZZFromTheScreen", true)""");

        await ClickAsync("#pbGo");

        // <b>One request, to the composition endpoint.</b> The old drawer needed three, in the
        // API's order; this is the assertion that the screen does not quietly do the same thing
        // with a nicer surface.
        await WaitForAsync(
            "(window.__writes || []).some(w => w.startsWith('POST') && w.includes('/admin/publish'))",
            "Publishing did not send a composition. The recorded writes were: "
            + string.Join(" | ", await WritesAsync()));

        Assert.DoesNotContain(
            "/admin/featureservices",
            string.Join(" | ", await WritesAsync()),
            StringComparison.Ordinal);

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// A table already served is offered and refused, with the reason where the table is.
    /// </summary>
    /// <remarks>
    /// <b>Greyed rather than hidden, because a reader is looking for it.</b>
    /// <c>layer_table_unique</c> is global — one table is one layer on this server, ADR-057 §5i
    /// — and a table that vanishes from the tree makes somebody hunt for a row that is working
    /// as designed.
    /// </remarks>
    [Fact]
    public async Task A_table_already_served_is_shown_and_cannot_be_taken()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/publish", token);

        await WaitForAsync(
            "document.querySelectorAll('#pubDbTree [data-pubdb]').length > 0",
            "No registered database is listed.");

        await ClickAsync("#pubDbTree [data-pubdb]");

        await WaitForAsync(
            "document.querySelectorAll('#pubDbTree [data-pubtable]').length > 0",
            "Opening a database listed no tables.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "document.querySelectorAll('#pubDbTree [data-pubtable].used').length > 0"),
            "No table in this fixture is marked as already served, so this test is not checking "
            + "anything. The seed publishes what it makes, so at least one should be.");

        string used = await Browser.EvaluateAsync<string>(
            "document.querySelector('#pubDbTree [data-pubtable].used')"
            + "?.getAttribute('data-pubtable') || ''") ?? string.Empty;

        Assert.False(string.IsNullOrWhiteSpace(used), "No served table to press.");

        await ClickAsync($"#pubDbTree [data-pubtable='{used}']");

        Assert.Equal(
            0,
            await Browser.EvaluateAsync<int>(
                "document.querySelectorAll('#pubTree [data-pubnode]').length"));

        NothingWentWrong(await PageErrorsAsync());
    }
}
