using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Host;
using Graticula.Providers.PostGis;
using Npgsql;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-300 §5.3: the member a layer statement is read for travels with every command and every connection of a pool,
/// and a pooled connection never carries one request's name into the next.
/// </summary>
/// <remarks>
/// <b>The mechanism, against PostgreSQL.</b> A table and a view filtered as a per-reader view is, read through one pool of
/// one connection — so every read below reuses the same backend — as two members in turn, as nobody, and streamed with
/// <see cref="CommandBehavior.SequentialAccess"/>, the path <c>PostGisFeatureSource</c>'s note says a leading <c>SET</c>
/// could shift.
/// </remarks>
[Trait("Needs", "RunningHost")]
public sealed class ReaderSessionTests
{
    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is not set, so this test FAILS rather than skips. It needs a PostgreSQL database.");

    [Fact]
    public async Task Two_members_reading_in_turn_through_one_pooled_connection_each_see_their_own_rows_and_nobody_sees_none()
    {
        NpgsqlConnectionStringBuilder single = new(Required("GRATICULA_TEST_PG")) { MaxPoolSize = 1, NoResetOnClose = false };
        await using NpgsqlDataSource pool = NpgsqlDataSource.Create(single.ConnectionString);
        string table = "zz_reader_" + Guid.NewGuid().ToString("N")[..8];

        await using (NpgsqlCommand make = pool.CreateCommand(
            $"create table public.{table} (id integer primary key, created_user text, payload text); "
            + $"insert into public.{table} values (1, 'alice', 'a1'), (2, 'bob', 'b1'), (3, 'alice', 'a2'), (4, '', 'empty'); "
            + $"create view public.{table}_v as select * from public.{table} where created_user = {ReaderSession.Expression}"))
        {
            await make.ExecuteNonQueryAsync();
        }

        try
        {
            async Task<List<string>> StreamedAsync()
            {
                List<string> seen = [];
                await using NpgsqlCommand read = pool.CreateReaderCommand($"select id, payload from public.{table}_v where id > @floor order by id");
                read.Parameters.AddWithValue("floor", 0);
                await using NpgsqlDataReader reader = await read.ExecuteReaderAsync(CommandBehavior.SequentialAccess);

                while (await reader.ReadAsync())
                {
                    _ = reader.GetInt32(0);
                    seen.Add(reader.GetString(1));
                }

                return seen;
            }

            async Task<List<string>> ThroughConnectionAsync()
            {
                List<string> seen = [];
                await using NpgsqlConnection connection = await pool.OpenReaderConnectionAsync();
                await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
                await using NpgsqlCommand read = new($"select payload from public.{table}_v order by id", connection, transaction);
                await using NpgsqlDataReader reader = await read.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    seen.Add(reader.GetString(0));
                }

                return seen;
            }

            using (ReaderSession.Enter("alice"))
            {
                Assert.Equal(["a1", "a2"], await StreamedAsync());
                Assert.Equal(["a1", "a2"], await ThroughConnectionAsync());
            }

            using (ReaderSession.Enter("bob"))
            {
                Assert.Equal(["b1"], await StreamedAsync());
                Assert.Equal(["b1"], await ThroughConnectionAsync());
            }

            // Nobody: no row, not even the one whose creator is empty — and not alice's from the connection before.
            using (ReaderSession.Enter("alice"))
            {
                Assert.Equal(["a1", "a2"], await ThroughConnectionAsync());
            }

            Assert.Empty(await StreamedAsync());
            Assert.Empty(await ThroughConnectionAsync());

            // A scalar and a row count are the caller's statement's, not the SET's.
            using (ReaderSession.Enter("alice"))
            {
                await using NpgsqlCommand count = pool.CreateReaderCommand($"select count(*) from public.{table}_v");
                Assert.Equal(2L, (long)(await count.ExecuteScalarAsync())!);
                await using NpgsqlCommand touch = pool.CreateReaderCommand($"update public.{table}_v set payload = payload");
                Assert.Equal(2, await touch.ExecuteNonQueryAsync());
            }

            // A name that would end the literal is a name, not SQL.
            using (ReaderSession.Enter("o'brien; drop table x"))
            {
                Assert.Empty(await StreamedAsync());
            }
        }
        finally
        {
            await using NpgsqlCommand drop = pool.CreateCommand($"drop view if exists public.{table}_v; drop table if exists public.{table}");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public void A_filter_naming_its_reader_parses_with_the_layers_own_parser_and_the_variable_stands_as_the_marker()
    {
        string[] columns = ["created_user", "assigned_to", "objectid"];
        Dictionary<string, FieldType> types = new() { ["created_user"] = FieldType.Text, ["assigned_to"] = FieldType.Text, ["objectid"] = FieldType.Integer };

        foreach (string written in (string[])["created_user = ${username}", "created_user = '${username}'", "assigned_to = ${username} or objectid = 1"])
        {
            Assert.True(ReaderFilters.Names(written));
            Assert.True(WhereClause.TryParse(ReaderFilters.Prepare(written), columns, LayerDefinition.Quote, out ParsedWhere parsed, out string? error, types), error);
            Assert.Contains(ReaderSession.Marker, parsed.Parameters);
            Assert.Null(ReaderFilters.Misplaced(parsed));
        }

        // Inside a longer text it is refused: by the parser, or — where the text still parses — as misplaced.
        bool parsedInside = WhereClause.TryParse(ReaderFilters.Prepare("created_user like '%${username}%'"), columns, LayerDefinition.Quote,
            out ParsedWhere inside, out _, types);
        Assert.False(parsedInside && ReaderFilters.Misplaced(inside) is null);
        Assert.Contains("whole value", ReaderFilters.Misplaced(new ParsedWhere("x = @w0", ["%" + ReaderSession.Marker + "%"])), StringComparison.Ordinal);

        Assert.False(WhereClause.TryParse(ReaderFilters.Prepare("nosuch = ${username}"), columns, LayerDefinition.Quote, out _, out string? refused, types));
        Assert.DoesNotContain(ReaderSession.Marker, ReaderFilters.Words(refused)!, StringComparison.Ordinal);
        Assert.False(ReaderFilters.Names("created_user = 'ali'"));
    }
}
