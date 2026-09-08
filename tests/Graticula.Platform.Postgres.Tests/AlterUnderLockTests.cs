using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Providers.PostGis;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A schema change on a table somebody is reading refuses quickly instead of queueing the table.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-058](../../docs/adr/ADR-058-the-datastore-schema-is-edited-from-the-screen.md)
/// condition 1.</b> <c>ALTER TABLE</c> takes <c>ACCESS EXCLUSIVE</c>. A request already reading
/// the table holds it off — and every request arriving <i>after</i> the DDL queues behind the
/// waiting DDL, so one blocked alteration stops the whole table for everybody.
/// </para>
/// <para>
/// <b>[D-08](../../docs/architecture-debt.md) measured that shape at 30.30 s against 0.296 s
/// unblocked</b>, which is the number this exists to keep away from. The importer sets
/// <c>lock_timeout</c> so the change is abandoned rather than made to wait; a person is watching
/// a screen, and a refusal they can retry beats a stall they cannot understand.
/// </para>
/// <para>
/// <b>Asserted on the clock as well as on the error.</b> The right <c>SqlState</c> arriving after
/// thirty seconds would be the failure this is about, wearing the code that says it is not.
/// </para>
/// </remarks>
public sealed class AlterUnderLockTests
{
    /// <summary>The raw connection string the suite is pointed at.</summary>
    private const string ConnectionVariable = "GRATICULA_TEST_PG";

    /// <summary>
    /// A held read makes the change refuse, and refuse in about the timeout rather than the
    /// statement timeout.
    /// </summary>
    [Fact]
    public async Task A_table_being_read_refuses_the_change_rather_than_queueing_behind_it()
    {
        string? configured = Environment.GetEnvironmentVariable(ConnectionVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(configured),
            $"{ConnectionVariable} is not set, so this test FAILS rather than skips. It needs one "
            + "connection string to a PostGIS database it may create a table in.");

        await using NpgsqlDataSource source = NpgsqlDataSource.Create(configured!);

        string table = "zzz_lock_" + Guid.NewGuid().ToString("N")[..8];

        // <b>Made through the importer, so the table is one it will agree to alter.</b> Its guard
        // refuses anything outside the schema this server creates into, which is the rule that
        // keeps it out of somebody else's data — and a test that reached around it would be
        // testing a path the product does not have.
        PostGisImporter importer = new(source);

        ImportResult made = await importer.DefineAsync(
            [new FieldDescription("note", FieldType.Text, true, null)],
            GeometryKind.Point,
            3857,
            table,
            CancellationToken.None);

        try
        {
            // A reader, held open. `ACCESS SHARE` is what a select takes and what an ALTER waits
            // for; nothing here is contrived, it is one ordinary request that has not finished.
            await using NpgsqlConnection reading = await source.OpenConnectionAsync();
            await using NpgsqlTransaction held = await reading.BeginTransactionAsync();

            await using (NpgsqlCommand select = new(
                $"select count(*) from hosted.\"{made.TableName}\"", reading, held))
            {
                await select.ExecuteScalarAsync();
            }

            Stopwatch clock = Stopwatch.StartNew();

            PostgresException refused = await Assert.ThrowsAsync<PostgresException>(
                async () => await importer.AddFieldAsync(
                    made.SchemaName,
                    made.TableName,
                    new FieldDescription("added", FieldType.Text, true, null),
                    CancellationToken.None));

            clock.Stop();

            Assert.True(
                refused.SqlState == "55P03",
                $"The change failed with {refused.SqlState} rather than 55P03 (lock_timeout): "
                + refused.MessageText);

            // <b>Ten seconds, against a two-second timeout.</b> Loose on purpose — the assertion
            // is *it did not wait for the statement timeout*, and a machine under load can take
            // several seconds to do a thing that takes two. D-08's number is 30.30 s and that is
            // the shape this rules out, not a tight bound on scheduling.
            Assert.True(
                clock.Elapsed < TimeSpan.FromSeconds(10),
                $"The change was refused, but only after {clock.Elapsed.TotalSeconds:F1} s. The "
                + "point of the timeout is that it does not wait — a correct error arriving after "
                + "half a minute is D-08 with a better error code on it.");

            await held.RollbackAsync();

            // <b>And it works once the reader is gone</b>, which is what makes the refusal a
            // refusal rather than a permanent failure. An operator is told to try again; this is
            // the assertion that trying again is honest advice.
            string column = await importer.AddFieldAsync(
                made.SchemaName,
                made.TableName,
                new FieldDescription("added", FieldType.Text, true, null),
                CancellationToken.None);

            Assert.Equal("added", column);
        }
        finally
        {
            await importer.DropAsync(made.SchemaName, made.TableName, CancellationToken.None);
        }
    }
}
