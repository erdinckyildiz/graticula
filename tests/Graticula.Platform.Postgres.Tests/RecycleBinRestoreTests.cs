using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A recycled service comes back row for row — its own rows, rows that point at each other, and a link a delete set to
/// null — and many rows come back as fast as they went: ADR-203 condition 3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The restore wrote a statement per kept row, 0.2 ms each, and a service with twenty years of
/// daily usage keeps 7,303 rows: measured on a fixture of the showcase's size on 2026-10-31, 1.43 s to restore against
/// 0.27 s to delete. It now writes a statement per table and depth. These tests hold what that must not change.
/// </para>
/// <para>
/// <b>Two tables of the test's own</b>, because nothing the shipped catalogue roots at a service is set to null on a
/// delete — every such key points at a principal — so the relink path has no other way to be exercised: <c>zz_part</c>
/// cascades from the service and its rows name each other, and <c>zz_note</c> is set to null and must get its link back.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class RecycleBinRestoreTests : PostgresFixture
{
    private async Task<(PostgresRecycleBin Bin, Guid Service)> ReadyAsync(int usageDays)
    {
        await MigrateAsync();

        Guid owner = Guid.NewGuid();

        await using (NpgsqlCommand principal = DataSource.CreateCommand(
            "insert into principal (id, kind, name) values (@id, 'user', @name)"))
        {
            principal.Parameters.AddWithValue("id", owner);
            principal.Parameters.AddWithValue("name", "recycler_" + owner.ToString("N")[..8]);
            await principal.ExecuteNonQueryAsync();
        }

        Guid service = await SeedServiceAsync("zz_recycled_" + owner.ToString("N")[..6], null, owner);

        await using (NpgsqlCommand made = DataSource.CreateCommand(
            """
            create table if not exists zz_part (
              id int primary key, service_id uuid not null references service (id) on delete cascade,
              next int references zz_part (id), label text);
            create table if not exists zz_note (
              id int primary key, service_id uuid references service (id) on delete set null, text text);
            insert into zz_part values (1, @s, 2, 'İlk'), (2, @s, 1, 'ikinci ğüşıöç');
            insert into zz_note values (1, @s, 'kept, and its link put back'), (2, null, 'never linked');
            insert into service_usage (service_id, day, requests)
            select @s, d::date, 3 from generate_series(current_date - (@days - 1), current_date, interval '1 day') d;
            """))
        {
            made.Parameters.AddWithValue("s", service);
            made.Parameters.AddWithValue("days", usageDays);
            await made.ExecuteNonQueryAsync();
        }

        return (new PostgresRecycleBin(DataSource), service);
    }

    /// <summary>Everything the service's walk reaches, as one fingerprint.</summary>
    private async Task<string> FingerprintAsync(Guid service)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(
            """
            select md5(string_agg(x, '|' order by x)) from (
              select 'service' || to_jsonb(s)::text x from service s where id = @s
              union all select 'item' || to_jsonb(i)::text from item i where service_id = @s
              union all select 'usage' || to_jsonb(u)::text from service_usage u where service_id = @s
              union all select 'part' || to_jsonb(p)::text from zz_part p where service_id = @s
              union all select 'note' || to_jsonb(n)::text from zz_note n) all_rows
            """);
        command.Parameters.AddWithValue("s", service);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> CountAsync(string sql, Guid service)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("s", service);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task A_service_comes_back_row_for_row_with_rows_that_name_each_other_and_a_link_put_back()
    {
        (PostgresRecycleBin bin, Guid service) = await ReadyAsync(usageDays: 30);
        string before = await FingerprintAsync(service);

        Guid? entry = await bin.RecycleAsync(
            "service", service, service, "service", "zz", null, null, DateTimeOffset.UtcNow.AddDays(14), [], CancellationToken.None);

        Assert.NotNull(entry);
        Assert.Equal(0, await CountAsync("select count(*) from zz_part where service_id = @s", service));
        Assert.Equal(0, await CountAsync("select count(*) from zz_note where service_id = @s", service));
        Assert.Equal(2, await CountAsync("select count(*) from zz_note where @s is not null", service));

        Assert.Null(await bin.RestoreRowsAsync(entry!.Value, CancellationToken.None));

        Assert.Equal(before, await FingerprintAsync(service));
        Assert.Equal(1, await CountAsync("select count(*) from zz_note where service_id = @s", service));
        Assert.Null(await bin.FindAsync(entry.Value, CancellationToken.None));
    }

    [Fact]
    public async Task A_restore_that_collides_changes_nothing_and_says_why()
    {
        (PostgresRecycleBin bin, Guid service) = await ReadyAsync(usageDays: 3);

        Guid? entry = await bin.RecycleAsync(
            "service", service, service, "service", "zz", null, null, DateTimeOffset.UtcNow.AddDays(14), [], CancellationToken.None);

        // Somebody made a row with one of its ids since: the restore refuses on the id.
        await using (NpgsqlCommand taken = DataSource.CreateCommand(
            "alter table zz_part alter column service_id drop not null; insert into zz_part values (2, null, null, 'taken since')"))
        {
            await taken.ExecuteNonQueryAsync();
        }

        string? refused = await bin.RestoreRowsAsync(entry!.Value, CancellationToken.None);

        Assert.NotNull(refused);
        Assert.Contains("same name or id", refused, StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync("select count(*) from service where id = @s", service));
        Assert.Equal(0, await CountAsync("select count(*) from service_usage where service_id = @s", service));
        Assert.NotNull(await bin.FindAsync(entry.Value, CancellationToken.None));
    }

    /// <summary>
    /// Twenty years of daily usage come back in about the time they took to go — the cost condition 3 measured.
    /// </summary>
    /// <remarks>
    /// <b>A ratio and not a time</b>, so a slow machine does not fail it: a restore written a row at a time took 5.3 times
    /// as long as the delete on 7,303 rows, and one written a table at a time took 0.6 times. Three times is the line.
    /// </remarks>
    [Fact]
    public async Task Twenty_years_of_usage_rows_come_back_no_slower_than_three_times_their_delete()
    {
        (PostgresRecycleBin bin, Guid service) = await ReadyAsync(usageDays: 7300);

        Stopwatch deleting = Stopwatch.StartNew();
        Guid? entry = await bin.RecycleAsync(
            "service", service, service, "service", "zz", null, null, DateTimeOffset.UtcNow.AddDays(14), [], CancellationToken.None);
        deleting.Stop();

        Stopwatch restoring = Stopwatch.StartNew();
        Assert.Null(await bin.RestoreRowsAsync(entry!.Value, CancellationToken.None));
        restoring.Stop();

        Assert.Equal(7300, await CountAsync("select count(*) from service_usage where service_id = @s", service));
        Assert.True(
            restoring.Elapsed < deleting.Elapsed * 3 + TimeSpan.FromMilliseconds(100),
            $"Deleting took {deleting.ElapsedMilliseconds} ms and restoring {restoring.ElapsedMilliseconds} ms: the restore is "
            + "writing a row at a time again.");
    }
}
