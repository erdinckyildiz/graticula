using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Server › Settings › Style sources (ADR-094): nothing can be typed before the stored list arrives, and
/// Save sends the list.
/// </summary>
public sealed class StyleSourcesCardTests : ConsoleTest
{
    /// <summary>
    /// The box and its Save wait for the stored list, then the list typed is the list sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Found by walking the card on 2026-09-29, before it shipped.</b> The box was on the screen and
    /// editable before <c>/admin/settings/style-origins</c> answered, and the answer then wrote the stored
    /// list over whatever had been typed: two lines typed at once were gone from the box by the time the
    /// server had refused one of them. The box and Save are now disabled in the markup and turned on by
    /// the answer, which is what the first half asserts.
    /// </para>
    /// <para>
    /// <b>The refusal itself is the server's and is tested there</b> (NamedStyleConformanceTests): this
    /// suite answers every write with <c>{}</c> so no click changes the server, which means a refusal
    /// never reaches the page here.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_box_waits_for_the_stored_list_and_Save_sends_it()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/settings", token);

        // Disabled in the markup, so there is no moment to type into before the list is in.
        Assert.Contains(
            "id=\"styleOrigins\" rows=\"4\" disabled",
            await Browser.EvaluateAsync<string>(
                "fetch('/server/index.html').then(r => r.text())") ?? string.Empty,
            StringComparison.Ordinal);

        await WaitForAsync(
            "document.getElementById('styleOrigins') && !document.getElementById('styleOrigins').disabled"
            + " && !document.getElementById('styleOriginsSave').disabled",
            "The style sources box never became editable once the stored list was read.");

        await Browser.EvaluateAsync<bool>(
            "(document.getElementById('styleOrigins').value = 'https://basemaps.example.com', true)");

        await ClickAsync("#styleOriginsSave");

        await WaitForAsync(
            "(window.__writes || []).some(w => w.startsWith('PUT') && w.includes('/admin/settings/style-origins'))",
            "Save sent nothing to /admin/settings/style-origins.");

        Assert.Single(await WritesAsync(), w => w.Contains("/admin/settings/style-origins", StringComparison.Ordinal));

        NothingWentWrong(await PageErrorsAsync());
    }
}
