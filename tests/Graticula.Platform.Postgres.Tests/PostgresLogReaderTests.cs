using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Postgres;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// Retention, and the one thing the conformance suite cannot assert.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything else about the logs is asserted from outside, through the API a console
/// reads</b> — see `LogConformanceTests`, and that is the better place for almost all of it.
/// This class exists for the assertion that cannot be made that way:
/// [ADR-045](../../docs/adr/ADR-045-the-server-keeps-a-log-you-can-ask-questions-of.md)
/// condition 3 is about a thirty-day window, and proving a thirty-day window needs a row
/// thirty days old. There is no request a test can make that produces one.
/// </para>
/// <para>
/// <b>So the row is written directly, and only the row's age is contrived.</b> Everything
/// else about it is what the writer would have written. A test that faked the sweep as well
/// as the row would be asserting its own arithmetic.
/// </para>
/// </remarks>
public sealed class PostgresLogReaderTests : PostgresFixture
{
    [Fact]
    public async Task The_sweep_takes_what_is_older_than_the_window_and_leaves_the_rest()
    {
        await MigrateAsync();

        // Two request rows and two studio rows: one of each old enough to go, one of each
        // young enough to stay. The boundary is what is being tested, so both sides of it
        // have to be present in the same sweep.
        await RequestAsync("/old/request", DateTimeOffset.UtcNow - TimeSpan.FromDays(40));
        await RequestAsync("/new/request", DateTimeOffset.UtcNow - TimeSpan.FromHours(1));
        await ClientAsync("old event", DateTimeOffset.UtcNow - TimeSpan.FromDays(40));
        await ClientAsync("new event", DateTimeOffset.UtcNow - TimeSpan.FromHours(1));

        PostgresLogReader reader = new(DataSource);

        long swept = await reader.SweepAsync(TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Equal(2, swept);

        // <b>Read back through the reader rather than with a count.</b> A `select count(*)`
        // would prove the delete; reading the log proves the reader and the delete agree,
        // which is what an operator actually experiences.
        LogQuery everything = new(null, null, null, null, null, null, null, false, null, 100);

        IReadOnlyList<LogRow> requests =
            await reader.RequestsAsync(everything, CancellationToken.None);

        Assert.Single(requests);
        Assert.Contains("/new/request", requests[0].What, StringComparison.Ordinal);

        IReadOnlyList<LogRow> events =
            await reader.ClientAsync(everything, CancellationToken.None);

        Assert.Single(events);
        Assert.Contains("new event", events[0].What, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_sweep_never_touches_the_audit_trail()
    {
        // <b>The deliberate asymmetry, asserted so that a future tidy-up cannot quietly
        // remove it.</b> *Who deleted that service last quarter* is the question the audit
        // trail exists for; a retention window that forgot it would make the trail
        // decorative. It is the one log with no cap, and that is a decision rather than an
        // omission — which is exactly the kind of thing somebody generalises away.
        await MigrateAsync();

        await AuditAsync("service.delete", DateTimeOffset.UtcNow - TimeSpan.FromDays(400));

        PostgresLogReader reader = new(DataSource);

        await reader.SweepAsync(TimeSpan.FromDays(30), CancellationToken.None);

        LogQuery everything = new(null, null, null, null, null, null, null, false, null, 100);

        IReadOnlyList<LogRow> audit = await reader.AuditAsync(everything, CancellationToken.None);

        Assert.Single(audit);
        Assert.Equal("service.delete", audit[0].What);
    }

    [Fact]
    public async Task A_filter_selects_and_a_cursor_pages_without_repeating()
    {
        await MigrateAsync();

        for (int i = 0; i < 6; i++)
        {
            await RequestAsync(
                $"/page/{i}",
                DateTimeOffset.UtcNow - TimeSpan.FromMinutes(i),
                status: i % 2 == 0 ? 200 : 500);
        }

        PostgresLogReader reader = new(DataSource);

        // <b>`failed` is a status band, not a column.</b> Anything 400 or above, so one
        // checkbox reads the same way on a log whose success is a boolean and one whose
        // success is a number.
        LogQuery failed = new(null, null, null, null, null, null, null, true, null, 100);

        IReadOnlyList<LogRow> bad = await reader.RequestsAsync(failed, CancellationToken.None);

        Assert.Equal(3, bad.Count);
        Assert.All(bad, row => Assert.False(row.Succeeded));

        // Two pages of two, and the second must not repeat the first.
        LogQuery first = new(null, null, null, null, null, null, null, false, null, 2);

        IReadOnlyList<LogRow> one = await reader.RequestsAsync(first, CancellationToken.None);

        Assert.Equal(2, one.Count);

        LogQuery second = new(
            null, null, null, null, null, null, null, false, one[^1].Cursor, 2);

        IReadOnlyList<LogRow> two = await reader.RequestsAsync(second, CancellationToken.None);

        Assert.Equal(2, two.Count);
        Assert.All(two, row => Assert.True(row.Cursor < one[^1].Cursor));
    }

    [Fact]
    public async Task A_free_text_filter_is_a_parameter_and_not_a_concatenation()
    {
        // <b>Not a demonstration that filtering works — that is the test above.</b> This one
        // sends the shapes that would end a statement early if the filter were pasted into
        // SQL, and asserts the server answers rather than failing. A log's free-text box is
        // the widest thing on the screen and the only one a caller writes.
        await MigrateAsync();

        await RequestAsync("/harmless", DateTimeOffset.UtcNow);

        PostgresLogReader reader = new(DataSource);

        foreach (string hostile in new[]
        {
            "' or 1=1 --",
            "'; drop table request_log; --",
            "100%",
            "_",
            "\\",
        })
        {
            LogQuery query = new(null, null, hostile, null, null, null, null, false, null, 10);

            // The assertion is that this returns at all. A concatenated filter would throw
            // a PostgresException on the first of these and drop a table on the second.
            IReadOnlyList<LogRow> rows =
                await reader.RequestsAsync(query, CancellationToken.None);

            Assert.NotNull(rows);
        }

        // And the table is still there, with its row in it.
        LogQuery everything = new(null, null, null, null, null, null, null, false, null, 10);

        Assert.Single(await reader.RequestsAsync(everything, CancellationToken.None));
    }

    private async Task RequestAsync(string path, DateTimeOffset at, int status = 200)
    {
        // <b>Written with an explicit `occurred_at`, which the writer never does.</b> That is
        // the whole reason this test is here rather than in the conformance suite: the column
        // defaults to `now()` and nothing a client can send moves it.
        const string Sql = """
            insert into request_log
              (occurred_at, method, path, query, status, duration_ms, principal_name, face)
            values (@at, 'GET', @path, null, @status, 7, 'root', 'ArcGIS')
            """;

        await using NpgsqlCommand command = DataSource.CreateCommand(Sql);
        command.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at });
        command.Parameters.AddWithValue("path", path);
        command.Parameters.AddWithValue("status", status);

        await command.ExecuteNonQueryAsync();
    }

    private async Task ClientAsync(string message, DateTimeOffset at)
    {
        const string Sql = """
            insert into client_event (occurred_at, kind, page, message, detail)
            values (@at, 'viewer', '/studio/map.html', @message, '{}'::jsonb)
            """;

        await using NpgsqlCommand command = DataSource.CreateCommand(Sql);
        command.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at });
        command.Parameters.AddWithValue("message", message);

        await command.ExecuteNonQueryAsync();
    }

    private async Task AuditAsync(string action, DateTimeOffset at)
    {
        const string Sql = """
            insert into audit_event
              (id, occurred_at, principal_name, action, resource, detail, succeeded)
            values (@id, @at, 'root', @action, 'a service', '{}'::jsonb, true)
            """;

        await using NpgsqlCommand command = DataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at });
        command.Parameters.AddWithValue("action", action);

        await command.ExecuteNonQueryAsync();
    }
}
