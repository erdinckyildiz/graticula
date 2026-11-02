using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Studio's Groups screen offers <i>Join group</i> — ADR-256 §5.7. The route <c>PUT /admin/groups/{name}/membership</c>
/// existed and the screen did not offer it, which is the shape D-67 names: a setting (*they add themselves*) stored and
/// shown, and no way for anybody to act on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asserted as the reader who needs it</b>: a plain <c>user</c> who is in neither group, at both widths the redesign
/// holds (ADR-255): 1,280 and a 390-pixel phone. A group that lets members add themselves is found under <i>Can join</i>
/// and joined from its page; one that takes members by invitation is not listed there and its page draws no Join.
/// </para>
/// <para>
/// <b>Visible, not present.</b> Every control is asserted with <see cref="ConsoleTest.Shown"/> — a control inside a
/// closed <c>details</c> or under <c>display: none</c> has passed tests here before while no user could see it — and on
/// the phone its height is measured against the 44-pixel rule.
/// </para>
/// <para>
/// <b>The server is asked as well as the page.</b> A page that redrew itself as a member while the request failed would
/// pass every assertion about the page.
/// </para>
/// </remarks>
public sealed class GroupJoinTests : ConsoleTest
{
    /// <summary>Text with its runs of white space made one, as a reader sees a wrapped sentence.</summary>
    private static string Flat(string? text) =>
        System.Text.RegularExpressions.Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();

    private static string Settings(string joinPolicy) => JsonSerializer.Serialize(new
    {
        title = (string?)null,
        summary = (string?)null,
        description = (string?)null,
        visibility = "organization",
        joinPolicy,
        contribute = "managers",
        deleteLocked = false,
    });

    private async Task<(int Status, string Body)> AsAsync(string token, HttpMethod method, string path, object? body = null)
    {
        using HttpRequestMessage request = new(method, new Uri($"{Root}{path}"));

        if (token.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await Http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>A plain member, signed in with their issued password replaced: a token the console accepts.</summary>
    private async Task<string> MemberAsync(string name)
    {
        (int made, string answer) = await AdminAsync(HttpMethod.Post, "/admin/members",
            JsonSerializer.Serialize(new { name, role = "user" }));
        Assert.True(made == 201, $"Making {name} answered {made}.");
        string issued = JsonDocument.Parse(answer).RootElement.GetProperty("password").GetString()!;

        (int s, string b) = await AsAsync(string.Empty, HttpMethod.Post, "/rest/auth/login", new { name, password = issued });
        Assert.True(s == 200, $"Signing in as {name} answered {s}.");
        string first = JsonDocument.Parse(b).RootElement.GetProperty("token").GetString()!;

        string chosen = "Zz" + Guid.NewGuid().ToString("N")[..20] + "!7";
        (s, _) = await AsAsync(first, HttpMethod.Post, "/rest/auth/password", new { currentPassword = issued, newPassword = chosen });
        Assert.True(s is >= 200 and < 300, $"Replacing {name}'s issued password answered {s}.");

        (s, b) = await AsAsync(string.Empty, HttpMethod.Post, "/rest/auth/login", new { name, password = chosen });
        Assert.True(s == 200, $"Signing in again as {name} answered {s}.");
        return JsonDocument.Parse(b).RootElement.GetProperty("token").GetString()!;
    }

    private async Task MakeGroupAsync(string name, string title, string joinPolicy)
    {
        (int made, string why) = await AdminAsync(HttpMethod.Post, "/admin/groups", JsonSerializer.Serialize(new { name, title }));
        Assert.True(made is 200 or 201, $"Making {name} answered {made}: {why}");
        (int set, string said) = await AdminAsync(HttpMethod.Put, $"/admin/groups/{name}/settings", Settings(joinPolicy));
        Assert.True(set == 200, $"Setting {name} to {joinPolicy} answered {set}: {said}");
    }

    /// <summary>Where this member stands in this group, as the server says.</summary>
    private async Task<string> StandingAsync(string token, string group)
    {
        (int s, string b) = await AsAsync(token, HttpMethod.Get, $"/admin/groups/{group}");
        Assert.True(s == 200, $"GET /admin/groups/{group} as the member answered {s}.");
        return JsonDocument.Parse(b).RootElement.GetProperty("standing").GetString()!;
    }

    private async Task SizeAsync(int width)
    {
        await Browser.CallAsync("Emulation.setDeviceMetricsOverride", new
        {
            width,
            height = width < 720 ? 844 : 800,
            deviceScaleFactor = 1,
            mobile = width < 720,
        });
    }

    private async Task<double> HeightAsync(string selector) => await Browser.EvaluateAsync<double>(
        $"document.querySelector({JsonSerializer.Serialize(selector)}).getBoundingClientRect().height");

    /// <summary>
    /// A member finds a group that lets members add themselves under <i>Can join</i>, joins it from its page and is shown
    /// as a member without a reload, with the focus on <i>Leave group</i>; leaves it, and is offered Join again. An
    /// invitation-only group is not under <i>Can join</i> and its page offers no Join.
    /// </summary>
    [Theory]
    [InlineData(1280)]
    [InlineData(390)]
    public async Task A_member_joins_and_leaves_a_group_that_lets_them_and_is_not_offered_one_that_does_not(int width)
    {
        string run = Guid.NewGuid().ToString("N")[..8];
        string member = "zz_join_" + run;
        string open = "zz_open_" + run;
        string closed = "zz_closed_" + run;
        bool phone = width < 720;

        try
        {
            await MakeGroupAsync(open, "Zz open group " + run, "self");
            await MakeGroupAsync(closed, "Zz closed group " + run, "invitation");
            string token = await MemberAsync(member);

            await SizeAsync(width);

            // <b>The list: the head's scope control, and Can join holding the open group and not the closed one.</b>
            await OpenAsync("/studio/#/groups", token);
            await WaitForAsync($"!!document.querySelector('#groupRows tr[data-group={open}]')",
                "All groups does not list an organization-visible group the member is outside.");
            await WaitForAsync(Shown("#groupScope [data-group-scope=join]"), "Groups draws no Can join control.");

            if (phone)
            {
                double scope = await HeightAsync("#groupScope [data-group-scope=join]");
                Assert.True(scope >= 44, $"Can join is {scope} pixels tall at 390; a phone's target is 44.");
                Assert.True(await Browser.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"),
                    "The Groups page scrolls sideways at 390 with the scope control in its head.");
            }

            await ClickAsync("#groupScope [data-group-scope=join]");
            await WaitForAsync(
                "document.querySelector('#groupScope [data-group-scope=join]').getAttribute('aria-pressed') === 'true'"
                + $" && !!document.querySelector('#groupRows tr[data-group={open}]')"
                + $" && !document.querySelector('#groupRows tr[data-group={closed}]')",
                "Can join does not hold the open group alone of the two.");
            Assert.Contains("can join", await Browser.EvaluateAsync<string>(
                $"document.querySelector('#groupRows tr[data-group={open}]').innerText") ?? string.Empty, StringComparison.Ordinal);

            // My groups: the member is in neither.
            await ClickAsync("#groupScope [data-group-scope=mine]");
            await WaitForAsync(
                $"!document.querySelector('#groupRows tr[data-group={open}]') && !document.querySelector('#groupRows tr[data-group={closed}]')"
                + " && !!document.querySelector('#groupRows td.empty')",
                "My groups lists a group the member is not in, or draws no sentence where it lists nothing.");

            // <b>The page: Join group, seen and pressed.</b>
            await OpenAsync($"/studio/#/group/{open}", token);
            await WaitForAsync(Shown("#groupJoin"), "A group that lets members add themselves draws no visible Join group.");

            // The harness answers every write itself unless told otherwise; this one has to reach the server.
            await Browser.EvaluateAsync<bool>($"(window.__letWritesTo = ['/admin/groups/{open}/membership'], true)");
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#groupLeave")), "A member outside the group is offered Leave group.");
            Assert.Equal("Join group", (await Browser.EvaluateAsync<string>("document.getElementById('groupJoin').textContent"))?.Trim());
            Assert.Contains("not a member", Flat(await Browser.EvaluateAsync<string>("document.getElementById('groupFacts').textContent")),
                StringComparison.Ordinal);

            if (phone)
            {
                double join = await HeightAsync("#groupJoin");
                Assert.True(join >= 44, $"Join group is {join} pixels tall at 390; a phone's target is 44.");
            }

            await ClickAsync("#groupJoin");
            await WaitForAsync(Shown("#groupLeave") + " && !" + Shown("#groupJoin")
                + " && /You are\\s*a member/.test(document.getElementById('groupFacts').textContent || '')",
                "After Join group the page does not show the reader as a member.");
            await WaitForAsync("document.activeElement?.id === 'groupLeave'",
                "After Join group the focus is not on Leave group, which stands where Join did.");
            Assert.Equal("member", await StandingAsync(token, open));

            if (phone)
            {
                double leave = await HeightAsync("#groupLeave");
                Assert.True(leave >= 44, $"Leave group is {leave} pixels tall at 390; a phone's target is 44.");
            }

            // <b>Leave, and the group the member can still find is redrawn with Join, not left behind.</b>
            await ClickAsync("#groupLeave");
            await WaitForAsync(Shown("#groupJoin") + " && !" + Shown("#groupLeave"),
                "After Leave group the page of a group the member may join again does not offer Join.");
            await WaitForAsync("document.activeElement?.id === 'groupJoin'", "After Leave group the focus is not on Join group.");
            Assert.Equal("outside", await StandingAsync(token, open));

            // <b>An invitation-only group: no Join, and no pretending.</b>
            await OpenAsync($"/studio/#/group/{closed}", token);
            await WaitForAsync("(document.getElementById('groupFacts')?.textContent || '').includes('not a member')",
                "An invitation-only group's page does not say the reader is not a member.");
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#groupJoin")), "An invitation-only group offers Join group.");
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#groupLeave")), "A group the reader is outside offers Leave group.");
            Assert.Contains("by invitation", Flat(await Browser.EvaluateAsync<string>("document.getElementById('groupFacts').textContent")),
                StringComparison.Ordinal);

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await Browser.CallAsync("Emulation.clearDeviceMetricsOverride");
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{open}");
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{closed}");
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{member}");
        }
    }
}
