using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Jobs;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A feature export's record: one budget with the tile exports, one export per caller decided with the insert,
/// progress only from its holder, finished with a size and an expiry in one statement, cancelled only as an export,
/// removed once, and only its own caller's rows listed — migration 68, ADR-106.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FeatureExportStoreTests : PostgresFixture
{
    private const string Worker = "graticula/feature-export FeatureExportStoreTests#1";
    private const string Other = "graticula/feature-export FeatureExportStoreTests#2";

    private const long Plenty = long.MaxValue / 4;

    [Fact]
    public async Task An_export_starts_queued_with_its_token_layers_and_estimate()
    {
        (PostgresFeatureExportStore exports, _, Guid owner, Guid service) = await ReadyAsync();
        string token = Token();

        FeatureExportStart start = await exports.StartAsync(
            owner, Request(service, token), "exporting", "{}", Plenty, CancellationToken.None);

        FeatureExportState export = Assert.IsType<FeatureExportState>(start.Started);
        Assert.Null(start.RefusedForSpace);
        Assert.Null(start.AlreadyRunning);
        Assert.Equal(JobKind.FeatureExport, export.Job.Kind);
        Assert.Equal(JobStatus.Queued, export.Job.Status);
        Assert.Equal(owner, export.Job.Owner);
        Assert.Equal(FeatureExportFormat.GeoPackage, export.Format);
        Assert.Equal([0, 2, 3], export.Layers);
        Assert.Equal(300, export.RowsTotal);
        Assert.Equal(0, export.RowsWritten);
        Assert.Equal(FeatureExportPhase.Reading, export.Phase);
        Assert.Equal(token, export.Token);
        Assert.Equal("EarlyAlert.gpkg", export.FileName);
        Assert.Equal(4321, export.EstimatedBytes);
        Assert.Null(export.Bytes);
        Assert.Null(export.ExpiresAt);
    }

    [Fact]
    public async Task A_token_that_is_not_thirty_two_hex_characters_is_refused_by_the_table()
    {
        (PostgresFeatureExportStore exports, _, Guid owner, Guid service) = await ReadyAsync();

        // The names a path could be made of: the constraint is the last brace, whatever the caller checked.
        foreach (string bad in (string[])["../../etc/passwd", new string('A', 32), new string('0', 31), new string('0', 33)])
        {
            await Assert.ThrowsAnyAsync<Exception>(() => exports.StartAsync(
                owner, Request(service, bad), "bad", "{}", Plenty, CancellationToken.None));
        }
    }

    [Fact]
    public async Task One_budget_holds_the_tile_exports_and_the_feature_exports_together()
    {
        (PostgresFeatureExportStore features, _, Guid owner, Guid service) = await ReadyAsync();
        PostgresTileExportStore tiles = new(DataSource);

        long before = await features.HeldBytesAsync(CancellationToken.None);
        Assert.Equal(before, await tiles.HeldBytesAsync(CancellationToken.None));

        // A tile package of 1000 bytes is held against a feature export's budget...
        Assert.NotNull((await tiles.StartAsync(
            owner, TileRequest(service, Token(), 1000), "package", "{}", Plenty, CancellationToken.None)).Started);

        Assert.Equal(before + 1000, await features.HeldBytesAsync(CancellationToken.None));

        FeatureExportStart refused = await features.StartAsync(
            owner, Request(service, Token()), "too big", "{}", before + 1000 + 4320, CancellationToken.None);

        Assert.Null(refused.Started);
        Assert.Null(refused.AlreadyRunning);
        Assert.Equal(before + 1000, refused.RefusedForSpace);

        // ... exactly enough fits, and then the feature export's estimate is held against a tile package's budget.
        FeatureExportState started = (await features.StartAsync(
            owner, Request(service, Token()), "fits", "{}", before + 1000 + 4321, CancellationToken.None)).Started!;

        Assert.Equal(before + 1000 + 4321, await tiles.HeldBytesAsync(CancellationToken.None));

        TileExportStart tileRefused = await tiles.StartAsync(
            owner, TileRequest(service, Token(), 1), "one more byte", "{}", before + 1000 + 4321, CancellationToken.None);

        Assert.Null(tileRefused.Started);
        Assert.Equal(before + 1000 + 4321, tileRefused.RefusedForSpace);

        // A written file is held at its size, not its estimate.
        await ClaimAsync(new PostgresJobStore(DataSource), started.Job.Id, Worker);
        Assert.True(await features.FinishAsync(started.Job.Id, Worker, 100, 300, TimeSpan.FromHours(1), CancellationToken.None));
        Assert.Equal(before + 1000 + 100, await features.HeldBytesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_caller_has_one_export_queued_or_running_and_another_caller_is_not_blocked_by_it()
    {
        (PostgresFeatureExportStore exports, _, Guid owner, Guid service) = await ReadyAsync();
        Guid neighbour = await PrincipalAsync();

        FeatureExportState first = (await exports.StartAsync(
            owner, Request(service, Token()), "first", "{}", Plenty, CancellationToken.None)).Started!;

        FeatureExportStart second = await exports.StartAsync(
            owner, Request(service, Token()), "second", "{}", Plenty, CancellationToken.None);

        Assert.Null(second.Started);
        Assert.Equal(first.Job.Id, second.AlreadyRunning);
        Assert.Single(await exports.ListAsync(service, owner, 10, CancellationToken.None));

        // Another caller's export is theirs.
        Assert.NotNull((await exports.StartAsync(
            neighbour, Request(service, Token()), "theirs", "{}", Plenty, CancellationToken.None)).Started);

        // Once the first has ended, in any way, the caller may ask again.
        Assert.True(await exports.CancelAsync(first.Job.Id, CancellationToken.None));

        Assert.NotNull((await exports.StartAsync(
            owner, Request(service, Token()), "again", "{}", Plenty, CancellationToken.None)).Started);
    }

    [Fact]
    public async Task Listing_is_the_callers_own_unless_it_is_asked_for_everybodys()
    {
        (PostgresFeatureExportStore exports, _, Guid owner, Guid service) = await ReadyAsync();
        Guid neighbour = await PrincipalAsync();

        FeatureExportState mine = (await exports.StartAsync(
            owner, Request(service, Token()), "mine", "{}", Plenty, CancellationToken.None)).Started!;
        FeatureExportState theirs = (await exports.StartAsync(
            neighbour, Request(service, Token()), "theirs", "{}", Plenty, CancellationToken.None)).Started!;

        Assert.Equal([mine.Job.Id], (await exports.ListAsync(service, owner, 10, CancellationToken.None)).Select(e => e.Job.Id));
        Assert.Equal([theirs.Job.Id], (await exports.ListAsync(service, neighbour, 10, CancellationToken.None)).Select(e => e.Job.Id));
        Assert.Equal(
            new HashSet<Guid> { mine.Job.Id, theirs.Job.Id },
            (await exports.ListAsync(service, null, 10, CancellationToken.None)).Select(e => e.Job.Id).ToHashSet());
    }

    [Fact]
    public async Task Only_the_worker_holding_it_may_checkpoint_and_finishing_sets_the_size_and_expiry()
    {
        (PostgresFeatureExportStore exports, PostgresJobStore jobs, Guid owner, Guid service) = await ReadyAsync();

        FeatureExportState export = (await exports.StartAsync(
            owner, Request(service, Token()), "a", "{}", Plenty, CancellationToken.None)).Started!;

        Assert.False(await exports.CheckpointAsync(export.Job.Id, Worker, 1, FeatureExportPhase.Reading, 5, CancellationToken.None));

        await ClaimAsync(jobs, export.Job.Id, Worker);

        Assert.False(await exports.CheckpointAsync(export.Job.Id, Other, 1, FeatureExportPhase.Reading, 5, CancellationToken.None));
        Assert.True(await exports.CheckpointAsync(export.Job.Id, Worker, 120, FeatureExportPhase.Writing, 40, CancellationToken.None));

        FeatureExportState now = (await exports.FindAsync(export.Job.Id, CancellationToken.None))!;
        Assert.Equal((120, FeatureExportPhase.Writing, 40), (now.RowsWritten, now.Phase, now.Job.Progress));

        Assert.False(await exports.FinishAsync(export.Job.Id, Other, 999, 300, TimeSpan.FromHours(1), CancellationToken.None));
        Assert.True(await exports.FinishAsync(export.Job.Id, Worker, 999, 300, TimeSpan.FromHours(1), CancellationToken.None));

        FeatureExportState done = (await exports.FindAsync(export.Job.Id, CancellationToken.None))!;
        Assert.Equal(JobStatus.Done, done.Job.Status);
        Assert.Equal(999, done.Bytes);
        Assert.Equal(300, done.RowsWritten);
        Assert.NotNull(done.ExpiresAt);
        Assert.InRange(done.ExpiresAt!.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(50), TimeSpan.FromMinutes(70));
    }

    [Fact]
    public async Task A_cancelled_export_stops_answering_its_worker_and_cannot_be_finished()
    {
        (PostgresFeatureExportStore exports, PostgresJobStore jobs, Guid owner, Guid service) = await ReadyAsync();

        FeatureExportState export = (await exports.StartAsync(
            owner, Request(service, Token()), "a", "{}", Plenty, CancellationToken.None)).Started!;
        await ClaimAsync(jobs, export.Job.Id, Worker);

        Assert.True(await exports.CancelAsync(export.Job.Id, CancellationToken.None));
        Assert.False(await exports.CancelAsync(export.Job.Id, CancellationToken.None));
        Assert.False(await exports.CheckpointAsync(export.Job.Id, Worker, 1, FeatureExportPhase.Reading, 5, CancellationToken.None));
        Assert.False(await exports.FinishAsync(export.Job.Id, Worker, 1, 1, TimeSpan.FromHours(1), CancellationToken.None));

        Assert.Equal(JobStatus.Cancelled, (await exports.FindAsync(export.Job.Id, CancellationToken.None))!.Job.Status);
    }

    [Fact]
    public async Task Cancelling_a_feature_export_reaches_nothing_else()
    {
        (PostgresFeatureExportStore exports, PostgresJobStore jobs, Guid owner, Guid service) = await ReadyAsync();
        PostgresTileExportStore tiles = new(DataSource);

        TileExportState tile = (await tiles.StartAsync(
            owner, TileRequest(service, Token(), 10), "package", "{}", Plenty, CancellationToken.None)).Started!;

        // The statement is restricted to its own kind, as the tile export's is.
        Assert.False(await exports.CancelAsync(tile.Job.Id, CancellationToken.None));
        Assert.Equal(JobStatus.Queued, (await jobs.FindAsync(tile.Job.Id, owner, false, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task A_removed_export_is_marked_once_and_the_sweep_finds_the_expired_and_the_ended()
    {
        (PostgresFeatureExportStore exports, PostgresJobStore jobs, Guid owner, Guid service) = await ReadyAsync();
        Guid neighbour = await PrincipalAsync();

        FeatureExportState written = (await exports.StartAsync(
            owner, Request(service, Token()), "written", "{}", Plenty, CancellationToken.None)).Started!;
        FeatureExportState cancelled = (await exports.StartAsync(
            neighbour, Request(service, Token()), "cancelled", "{}", Plenty, CancellationToken.None)).Started!;

        await ClaimAsync(jobs, written.Job.Id, Worker);
        Assert.True(await exports.FinishAsync(written.Job.Id, Worker, 10, 10, TimeSpan.FromHours(1), CancellationToken.None));
        Assert.True(await exports.CancelAsync(cancelled.Job.Id, CancellationToken.None));

        // Not yet expired: only the cancelled one is due.
        IReadOnlyList<(Guid Job, string Token)> due =
            await exports.DueForRemovalAsync(DateTimeOffset.UtcNow, 1000, CancellationToken.None);

        Assert.DoesNotContain(due, d => d.Job == written.Job.Id);
        Assert.Contains(due, d => d.Job == cancelled.Job.Id && d.Token == cancelled.Token);

        due = await exports.DueForRemovalAsync(DateTimeOffset.UtcNow.AddHours(2), 1000, CancellationToken.None);
        Assert.Contains(due, d => d.Job == written.Job.Id && d.Token == written.Token);

        IReadOnlySet<string> live = await exports.LiveTokensAsync(CancellationToken.None);
        Assert.Contains(written.Token, live);

        Assert.True(await exports.MarkRemovedAsync(written.Job.Id, CancellationToken.None));
        Assert.False(await exports.MarkRemovedAsync(written.Job.Id, CancellationToken.None));

        Assert.DoesNotContain(written.Token, await exports.LiveTokensAsync(CancellationToken.None));
        Assert.DoesNotContain(
            await exports.DueForRemovalAsync(DateTimeOffset.UtcNow.AddHours(2), 1000, CancellationToken.None),
            d => d.Job == written.Job.Id);
    }

    [Fact]
    public async Task Deleting_the_service_takes_its_exports_with_it()
    {
        (PostgresFeatureExportStore exports, PostgresJobStore jobs, Guid owner, Guid service) = await ReadyAsync();

        FeatureExportState export = (await exports.StartAsync(
            owner, Request(service, Token()), "a", "{}", Plenty, CancellationToken.None)).Started!;

        await ExecuteAsync("delete from service where id = @id", service);

        Assert.Null(await exports.FindAsync(export.Job.Id, CancellationToken.None));
        Assert.NotNull(await jobs.FindAsync(export.Job.Id, owner, false, CancellationToken.None));
    }

    private static string Token() => Guid.NewGuid().ToString("N");

    private static FeatureExportRequest Request(Guid service, string token) =>
        new(service, FeatureExportFormat.GeoPackage, [0, 2, 3], RowsTotal: 300, EstimatedBytes: 4321, token, "EarlyAlert.gpkg");

    private static TileExportRequest TileRequest(Guid service, string token, long estimate) =>
        new(service, TileExportFormat.PmTiles, [2, 3, 5], new Envelope(-1000, -2000, 3000, 4000), Whole: false, Total: 21,
            EstimatedBytes: estimate, token, "webmercator", "arcgis");

    /// <summary>Claims one particular export, whatever else of the kind is queued in the shared schema.</summary>
    private async Task ClaimAsync(PostgresJobStore jobs, Guid id, string worker)
    {
        // Other exports queued by other tests would be claimed first; move this one to the front.
        await ExecuteAsync("update job set created_at = '1970-01-01' where id = @id", id);

        JobRecord? claimed = await jobs.ClaimAsync(JobKind.FeatureExport, worker, 1, CancellationToken.None);
        Assert.Equal(id, claimed?.Id);
    }

    private async Task<Guid> PrincipalAsync()
    {
        Guid id = Guid.NewGuid();

        await using NpgsqlCommand command = DataSource.CreateCommand(
            "insert into principal (id, kind, name, user_type) values (@id, 'user', @name, 'creator')");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", "zz_fexport_" + id.ToString("N")[..8]);
        await command.ExecuteNonQueryAsync();

        return id;
    }

    private async Task<(PostgresFeatureExportStore Exports, PostgresJobStore Jobs, Guid Owner, Guid Service)> ReadyAsync()
    {
        await MigrateAsync();

        Guid owner = await PrincipalAsync();
        Guid service = await SeedServiceAsync("fexported_" + owner.ToString("N")[..8], null, owner);

        return (new PostgresFeatureExportStore(DataSource), new PostgresJobStore(DataSource), owner, service);
    }

    private async Task ExecuteAsync(string sql, Guid id)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }
}
