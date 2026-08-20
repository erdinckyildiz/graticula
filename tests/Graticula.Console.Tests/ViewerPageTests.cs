using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The two standalone viewers, opened and asked whether they ran at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written because one of them had been dead for days and the suite was green
/// throughout.</b> <c>view.js</c> declared <c>const OSM_TILES</c> while
/// <c>ground.js</c>, loaded before it on the same page, declared the same name in the
/// same global lexical scope. That is a <c>SyntaxError</c> raised while parsing, so
/// not one line of the file executed: the page painted its static header and nothing
/// else — no map, no facts, no picker, and no message anywhere a person would look.
/// Every <em>View in: Map</em> link on every service page led to it.
/// </para>
/// <para>
/// <b>The gap was structural rather than bad luck.</b>
/// <see cref="EveryScreenTests"/> exists for exactly this class of defect and covers
/// the console's tabs, which are hash routes inside <c>console.js</c>. These two are
/// separate documents with their own scripts, so they were outside the loop and
/// nothing else opened them. A viewer is a page like any other and now gets the same
/// three questions.
/// </para>
/// <para>
/// <b>What is asserted is that the script ran, not that a map is pretty.</b> Whether
/// tiles arrive depends on a CDN and on a network this suite must not require; whether
/// the header filled in depends only on this repository's own code. So the checks are:
/// the page threw nothing, and the elements the script is responsible for stopped
/// being empty.
/// </para>
/// </remarks>
public sealed class ViewerPageTests : ConsoleTest
{
    /// <summary>
    /// The two viewers, and the element each fills in that proves its script ran.
    /// </summary>
    /// <remarks>
    /// <c>#facts</c> on both: it is written last, after the service document has been
    /// read and the layer built, so a non-empty one means the whole path ran. Its
    /// contents differ per face and per layer and are deliberately not asserted.
    /// </remarks>
    private const string Facts = "(document.getElementById('facts')?.textContent || '').trim()";

    [Theory]
    [InlineData("/studio/view.html", "")]
    [InlineData("/studio/view.html", "&face=mapserver")]
    [InlineData("/studio/map.html", "")]
    [InlineData("/studio/map.html", "&face=mapserver")]
    public async Task A_viewer_runs_its_script_and_describes_what_it_drew(string page, string extra)
    {
        (string token, string cookie) = await SignInAsync();

        string service = await AnyServiceAsync();

        // <b>The cookie, because that is the credential these pages actually have.</b>
        // A viewer is reached by clicking a link in the REST directory, so it is a
        // browser navigation: `fetch` here carries the session cookie and nothing else.
        // Signing in with only the bearer token would leave the page anonymous and it
        // would refuse the first private service the directory lists, which is what this
        // test did on its first run.
        await OpenAsync(
            $"{page}?service={Uri.EscapeDataString(service)}{extra}", token, cookie);

        // <b>The failure this class was written for is visible here and nowhere else.</b>
        // A parse error leaves every element exactly as the HTML shipped it, so the page
        // looks like one that is still loading rather than one that never started.
        await WaitForAsync(
            $"{Facts}.length > 0",
            $"{page}{extra} on '{service}' filled in nothing. Its script either did not run "
            + "or did not reach the end. A SyntaxError does the first and is invisible on the "
            + "page, which is how this went unnoticed until 2026-08-21.");

        string[] failures = await PageErrorsAsync();

        // A viewer that draws from a CDN cannot be asked to draw without one, so a
        // message about the SDK not loading is a correct answer rather than a failure.
        // What must not appear is anything from this repository's own files.
        string[] ours = [.. failures
            .Distinct()
            .Where(f => !f.Contains("js.arcgis.com", StringComparison.OrdinalIgnoreCase))];

        Assert.True(
            ours.Length == 0,
            $"{page}{extra} threw:\n  " + string.Join("\n  ", ours));
    }

    /// <summary>
    /// The MapServer face draws server-side, and says so where the count would be.
    /// </summary>
    /// <remarks>
    /// <b>Because the two faces are worth telling apart from outside.</b> A FeatureServer
    /// view counts features it fetched; a MapServer view counts sublayers somebody else
    /// drew. If <c>face=mapserver</c> ever silently fell back to the feature path — which
    /// is what a mistyped parameter would do — the page would still work and would be
    /// showing the wrong thing, which is the defect class the 2026-08-20 gates existed
    /// for.
    /// </remarks>
    [Theory]
    [InlineData("/studio/view.html")]
    [InlineData("/studio/map.html")]
    public async Task The_map_face_says_the_drawing_happened_on_the_server(string page)
    {
        (string token, string cookie) = await SignInAsync();

        string service = await AnyServiceAsync();

        await OpenAsync(
            $"{page}?service={Uri.EscapeDataString(service)}&face=mapserver", token, cookie);

        await WaitForAsync(
            $"{Facts}.toLowerCase().includes('server-side')",
            $"{page} was asked for the MapServer face of '{service}' and never said the "
            + "drawing was server-side. Either it fell back to the feature path, or it "
            + "stopped describing which face it is showing.");
    }

    /// <summary>Any service with at least one drawable layer.</summary>
    /// <remarks>
    /// Taken from the directory rather than named, because a fixture named here is a
    /// fixture that goes stale in a repository whose test data is republished.
    /// </remarks>
    private async Task<string> AnyServiceAsync()
    {
        (string Folder, string[] Services)[] folders = await FoldersWithServicesAsync();

        string? found = folders
            .SelectMany(entry => entry.Services)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

        Assert.True(found is not null, "No service is published, so this asserted nothing.");

        return found!;
    }
}
