using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// ADR-135: an item's usage is said on its page, My content orders by least used and filters what nobody used in thirty
/// days.
/// </summary>
public sealed class ContentUsageTests : ConsoleTest
{
    [Fact]
    public async Task Usage_is_said_on_the_item_and_idle_items_are_found()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/studio/#/content/mine", token);
        await WaitForAsync("document.querySelectorAll('#contentRows tr').length > 0", "My content did not open.");

        // The filter and the order are offered, and choosing the order says what it does.
        Assert.True(await Browser.EvaluateAsync<bool>("!!document.getElementById('contentIdle')"),
            "My content offers no way to find what nobody has used.");

        // On a server that began counting today nothing is idle yet, and the list says why rather than overclaiming.
        await Browser.EvaluateAsync<bool>(
            "(() => { const c = document.getElementById('contentIdle'); c.checked = true; c.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");
        await WaitForAsync("/Counting began|counted since|Counting begins/.test(document.getElementById('contentNote').textContent)",
            "The idle filter does not say what it is measured from.");
        await Browser.EvaluateAsync<bool>(
            "(() => { const c = document.getElementById('contentIdle'); c.checked = false; c.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");
        await Browser.EvaluateAsync<bool>(
            "(() => { const s = document.getElementById('contentSort'); s.value = 'leastUsed'; s.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");
        await WaitForAsync("document.getElementById('contentNote').textContent.includes('Least used first')",
            "Ordering by least used did not say what it does.");
        await WaitForAsync("/requests? (in 30 days|since)/.test(document.getElementById('contentRows').innerText)",
            "The least-used order does not show each service's count.");

        // A service's page says its usage.
        await Browser.EvaluateAsync<bool>(
            "(() => { const a = [...document.querySelectorAll('#contentRows td.name a')].find(x => x.getAttribute('href').startsWith('#/service/')); a.click(); return true; })()");
        await WaitForAsync("[...document.querySelectorAll('#svcFacts dt')].some(d => d.textContent === 'Usage')",
            "A service's page does not say its usage.");

        NothingWentWrong(await PageErrorsAsync());
    }
}
