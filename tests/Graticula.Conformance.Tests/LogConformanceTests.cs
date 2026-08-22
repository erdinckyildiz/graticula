using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The three logs, asked from outside.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every assertion here goes through the API a console reads, not through the
/// database.</b> A test that inserted a row and then selected it back would prove the
/// schema and nothing else;
/// [ADR-045](../../docs/adr/ADR-045-the-server-keeps-a-log-you-can-ask-questions-of.md)'s
/// conditions are about what a request does and what a reader can then find, which is a
/// round trip through the whole thing.
/// </para>
/// <para>
/// <b>In the catalogue-walk collection.</b> These read logs that every other test in this
/// assembly is writing to, so they must not run beside a class that reconfigures live
/// services — [D-75](../../docs/architecture-debt.md).
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class LogConformanceTests : ArcGisClient
{
    private async Task<(HttpStatusCode Status, string Body)> FetchAsync(string path)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + path));

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_token_in_a_query_string_never_reaches_the_request_log()
    {
        // <b>ADR-045 condition 2, and the reason it is a condition.</b> Esri clients send a
        // session token as `?token=` because that is how they have always worked
        // ([D-120](../../docs/architecture-debt.md)), so the query string of an ordinary
        // request is a credential. Persisting request lines put that into a table with an
        // index on it — which is the same debt in a worse place.
        //
        // <b>Asserted the same way `TokenIsNotLoggedTests` asserts it of the text log</b>,
        // because it is the same mechanism: one `QueryRedaction.Redact` call, used twice.
        string root = await RequireServerAsync();
        string sentinel = "SENTINEL" + Guid.NewGuid().ToString("N");

        // <b>A second parameter that is not a secret, so the row can be found again.</b>
        // Polling for the path was the first attempt and it is useless: every suite in this
        // assembly hits `/rest/info`, so the wait returned somebody else's row and the
        // tokened one had not arrived yet. The one thing that cannot be polled for is the
        // token itself — the whole point is that it is gone.
        string mark = "mark" + Guid.NewGuid().ToString("N")[..10];

        using HttpRequestMessage carrying = new(
            HttpMethod.Get, new Uri($"{root}/rest/info?f=json&mark={mark}&token={sentinel}"));

        using HttpResponseMessage answered = await Http.SendAsync(carrying);

        Assert.True(answered.IsSuccessStatusCode, "The request that carries the token failed.");

        // The writer batches, so the row is not there the instant the response is.
        string body = await EventuallyAsync("/admin/logs/requests?limit=200", mark);

        // <b>The row is present</b> — without this the test passes against a server that
        // records nothing at all, which is the failure it would least like to miss.
        Assert.Contains(mark, body, StringComparison.Ordinal);

        // <b>And the token is not, in any form.</b>
        Assert.DoesNotContain(sentinel, body, StringComparison.Ordinal);
        Assert.Contains("REDACTED", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_request_log_records_what_was_asked_and_how_long_it_took()
    {
        string root = await RequireServerAsync();
        string mark = "mark" + Guid.NewGuid().ToString("N")[..8];

        using HttpRequestMessage request = new(
            HttpMethod.Get, new Uri($"{root}/rest/services/{mark}/FeatureServer?f=json"));

        using HttpResponseMessage response = await Http.SendAsync(request);

        string body = await EventuallyAsync("/admin/logs/requests?limit=200", mark);

        JsonElement row = FindAsync(body, mark);

        // The columns an operator filters on, present and plausible.
        Assert.Contains("GET", row.GetProperty("what").GetString()!, StringComparison.Ordinal);
        Assert.False(row.GetProperty("ok").GetBoolean(), "A 404 was recorded as a success.");

        JsonElement detail = JsonDocument.Parse(row.GetProperty("detail").GetString()!).RootElement;

        Assert.True(detail.GetProperty("durationMs").GetInt32() >= 0);
        Assert.Equal("ArcGIS", detail.GetProperty("face").GetString());
    }

    [Fact]
    public async Task The_studio_reports_a_failure_the_server_never_saw_and_cannot_flood_it()
    {
        /*
          <b>One test, because the two halves cannot be independent and pretending they were
          made both of them flaky.</b> They were written as *the studio can report* and *the
          endpoint refuses a flood*, and the rate limit is per source address while every
          test in this suite comes from the same one — so whichever ran second found the
          minute's budget already spent. The flood test saw nothing stored and read it as a
          broken endpoint; the report test saw its one event refused and waited five seconds
          for a row that was never coming.

          <b>Merging makes the dependency explicit instead of accidental.</b> Accept first,
          then spend the budget deliberately.
        */
        string root = await RequireServerAsync();

        // ---------------------------------------------------------------- it accepts one
        //
        // <b>The case this log exists for.</b> On 2026-08-22 the viewer reset its own view on
        // every click, because two elements shared an id. Every request returned 200 and no
        // server-side log could ever have shown it.
        string sentinel = "viewer failed " + Guid.NewGuid().ToString("N")[..10];

        // <b>Retried across one window boundary, and only if it has to be.</b> A suite re-run
        // inside the same minute starts with the budget already spent, which is the
        // throttle working rather than a defect — so the common path is instant and the
        // unlucky one waits for the next window instead of reporting a false failure.
        string body = string.Empty;

        for (int window = 0; window < 2; window++)
        {
            await ReportAsync(root, "viewer", sentinel);

            body = await EventuallyAsync("/admin/logs/studio?limit=50", sentinel);

            if (body.Contains(sentinel, StringComparison.Ordinal))
            {
                break;
            }

            // The throttle's window is a whole minute of wall clock, so this is the wait.
            await Task.Delay(TimeSpan.FromSeconds(62));
        }

        Assert.Contains(sentinel, body, StringComparison.Ordinal);

        // ---------------------------------------------------------------- and refuses a flood
        //
        // <b>ADR-045 condition 4.</b> This is the one write in this server a stranger can
        // reach, so the bounds are the feature rather than a precaution.
        string flood = "flood" + Guid.NewGuid().ToString("N")[..8];

        // A body far past the 8 KB cap. The server reads up to the cap, so what it has is
        // truncated JSON that will not parse, and the event is dropped rather than stored
        // in pieces.
        using HttpRequestMessage huge = new(HttpMethod.Post, new Uri($"{root}/rest/studio/events"))
        {
            Content = new StringContent(
                "{\"kind\":\"error\",\"message\":\"" + flood + new string('x', 64 * 1024) + "\"}",
                Encoding.UTF8,
                "application/json"),
        };

        using HttpResponseMessage refusedHuge = await Http.SendAsync(huge);

        // <b>204 whatever happens, including a refusal.</b> A page reporting its own errors
        // must not be told anything it could act on: a distinct status for *throttled* would
        // let a caller measure the limit, and an error body would give a page in a render
        // loop something new to fail on.
        Assert.Equal(HttpStatusCode.NoContent, refusedHuge.StatusCode);

        // <b>Past the limit, which is 60 a minute from one address.</b> 90 attempts must not
        // become 90 rows.
        for (int i = 0; i < 90; i++)
        {
            await ReportAsync(root, "flood", flood + " " + i.ToString(CultureInfo.InvariantCulture));
        }

        (_, string stored) = await FetchAsync($"/admin/logs/studio?q={flood}&limit=200");

        int rows = JsonDocument.Parse(stored).RootElement.GetProperty("rows").GetArrayLength();

        Assert.True(rows < 90, $"All 90 attempts were stored; the rate limit did nothing.");

        // And the oversized one is not among them, at any length.
        Assert.DoesNotContain(new string('x', 100), stored, StringComparison.Ordinal);
    }

    /// <summary>Posts one studio event and asserts only that the endpoint answered 204.</summary>
    private async Task ReportAsync(string root, string kind, string message)
    {
        using HttpRequestMessage report = new(
            HttpMethod.Post, new Uri($"{root}/rest/studio/events"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    kind,
                    page = "/studio/map.html?face=imageserver",
                    message,
                    detail = new { where = "a conformance test" },
                }),
                Encoding.UTF8,
                "application/json"),
        };

        using HttpResponseMessage answered = await Http.SendAsync(report);

        Assert.Equal(HttpStatusCode.NoContent, answered.StatusCode);
    }

    [Fact]
    public async Task The_index_says_how_much_the_request_log_has_dropped()
    {
        // <b>ADR-045 condition 6.</b> The request log is lossy under load by design, so a
        // screen that showed its rows without showing what it lost would be claiming a
        // completeness it does not have. The number may be zero; the field may not be absent.
        (HttpStatusCode status, string body) = await FetchAsync("/admin/logs");

        Assert.Equal(HttpStatusCode.OK, status);

        JsonElement root = JsonDocument.Parse(body).RootElement;
        JsonElement writer = root.GetProperty("writer");

        Assert.True(writer.GetProperty("dropped").GetInt64() >= 0);
        Assert.True(writer.GetProperty("waiting").GetInt64() >= 0);

        // The actions a filter offers, counted — the audit trail has dozens.
        Assert.NotEqual(0, root.GetProperty("actions").GetArrayLength());
    }

    [Fact]
    public async Task A_log_this_server_does_not_keep_is_refused_by_name()
    {
        (HttpStatusCode status, string body) = await FetchAsync("/admin/logs/syslog");

        Assert.Equal(HttpStatusCode.BadRequest, status);

        string message = JsonDocument.Parse(body).RootElement
            .GetProperty("error").GetProperty("message").GetString() ?? string.Empty;

        Assert.Contains("syslog", message, StringComparison.Ordinal);
        Assert.Contains("audit", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reading_a_log_needs_the_privilege_and_says_so()
    {
        // A log carries principals, source addresses and paths — most of what somebody
        // probing a deployment wants to know.
        string root = await RequireServerAsync();

        foreach (string path in new[] { "/admin/logs", "/admin/logs/audit", "/admin/logs/requests" })
        {
            using HttpRequestMessage anonymous = new(HttpMethod.Get, new Uri(root + path));
            using HttpResponseMessage refused = await Http.SendAsync(anonymous);

            Assert.False(
                refused.IsSuccessStatusCode,
                $"{path} answered an unauthenticated caller with {(int)refused.StatusCode}.");
        }
    }

    [Fact]
    public async Task The_audit_trail_can_be_filtered_to_one_action()
    {
        // <b>ADR-045 condition 5's server half.</b> *Who deleted that service* is the
        // question this trail has been able to answer for weeks and nothing could ask.
        (_, string index) = await FetchAsync("/admin/logs");

        JsonElement actions = JsonDocument.Parse(index).RootElement.GetProperty("actions");

        if (actions.GetArrayLength() == 0)
        {
            return;
        }

        string action = actions[0].GetProperty("action").GetString()!;

        (HttpStatusCode status, string body) = await FetchAsync(
            $"/admin/logs/audit?action={Uri.EscapeDataString(action)}&limit=20");

        Assert.Equal(HttpStatusCode.OK, status);

        JsonElement rows = JsonDocument.Parse(body).RootElement.GetProperty("rows");

        Assert.NotEqual(0, rows.GetArrayLength());

        // Every row is the action that was asked for, and nothing else.
        foreach (JsonElement row in rows.EnumerateArray())
        {
            Assert.Equal(action, row.GetProperty("what").GetString());
        }
    }

    [Fact]
    public async Task Paging_by_cursor_does_not_repeat_a_row()
    {
        // <b>Why the cursor exists rather than an offset.</b> A log grows at the head while
        // it is being read, so an offset walks backwards through a list moving forwards and
        // page two repeats or skips. This asserts the property, not the mechanism.
        (_, string first) = await FetchAsync("/admin/logs/audit?limit=5");

        JsonElement page = JsonDocument.Parse(first).RootElement;

        if (page.GetProperty("rows").GetArrayLength() < 5)
        {
            return;
        }

        long before = page.GetProperty("next").GetInt64();

        (_, string second) = await FetchAsync($"/admin/logs/audit?limit=5&before={before}");

        JsonElement next = JsonDocument.Parse(second).RootElement;

        foreach (JsonElement row in next.GetProperty("rows").EnumerateArray())
        {
            Assert.True(
                row.GetProperty("cursor").GetInt64() < before,
                "A second page returned a row the first page had already shown.");
        }
    }

    /// <summary>Reads a log until it holds a mark, because the writer batches.</summary>
    private async Task<string> EventuallyAsync(string path, string mark)
    {
        string body = string.Empty;

        // <b>Polled rather than waited once.</b> The flusher wakes on a full batch or every
        // two seconds, so the row arrives within a couple of seconds of the request and a
        // fixed sleep would be either flaky or slow.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            (_, body) = await FetchAsync(path);

            if (body.Contains(mark, StringComparison.Ordinal))
            {
                return body;
            }

            await Task.Delay(250);
        }

        return body;
    }

    private static JsonElement FindAsync(string body, string mark)
    {
        foreach (JsonElement row in JsonDocument.Parse(body).RootElement
            .GetProperty("rows").EnumerateArray())
        {
            if ((row.GetProperty("what").GetString() ?? string.Empty)
                .Contains(mark, StringComparison.Ordinal))
            {
                return row;
            }
        }

        Assert.Fail($"No row in the request log mentions {mark}.");

        return default;
    }
}
