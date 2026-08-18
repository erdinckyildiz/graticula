using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// Removing a member, and what happens to what they owned.
/// </summary>
/// <remarks>
/// <b>ADR-015 §6c, owner decision 2026-08-18.</b> A member who owns nothing goes outright; one who
/// owns something is refused unless the caller said whether to transfer it or take it along. These
/// tests are the port's half — the refusals and the ownership arithmetic. The endpoint's half, which
/// orchestrates the <c>delete</c> disposition through the unpublish path so tiles are purged, is
/// measured against the running server and recorded on the ADR.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class MemberRemovalTests : PostgresFixture
{
    // Named rather than inline: CA1861 is on, and an expectation with a name reads better in a
    // failure than a bare array literal does.
    private static readonly string[] TheirService = ["their_service"];
    private static readonly string[] TheirFolder = ["their_folder"];
    private static readonly string[] InsideShelf = ["shelf/inside"];
    private static readonly string[] Moving = ["moving"];
    private static readonly string[] MovingFolder = ["moving_folder"];
    private static readonly string[] Untouched = ["untouched"];

    /// <summary>
    /// A member who owns nothing is removed, and the cascades take their credential with them.
    /// </summary>
    [Fact]
    public async Task A_member_who_owns_nothing_is_removed_outright()
    {
        PostgresMemberDirectory members = await ReadyAsync();

        await MakeAsync(members, "leaver", "publisher");

        MemberHoldings? holdings =
            await members.HoldingsOfAsync("leaver", CancellationToken.None);

        Assert.NotNull(holdings);
        Assert.False(holdings!.Value.Any);
        Assert.Equal("nothing", holdings.Value.Explanation);

        Assert.Equal(
            MemberRemoval.Removed,
            await members.RemoveAsync("leaver", CancellationToken.None));

        Assert.Null(await members.HoldingsOfAsync("leaver", CancellationToken.None));

        // The credential cascades. A row left behind would be a password for an account that no
        // longer exists, which is worse than either state on its own.
        Assert.Equal(0L, await ScalarAsync(
            $"select count(*) from {SchemaName}.local_credential c "
            + $"left join {SchemaName}.principal p on p.id = c.principal_id where p.id is null"));
    }

    /// <summary>
    /// A member who owns something is refused, and the refusal names what they own.
    /// </summary>
    /// <remarks>
    /// <b>This is the decision.</b> The owner asked for the removal to *say* what is attached and
    /// *ask* what to do — so the port answers <see cref="MemberRemoval.HoldsThings"/> rather than
    /// choosing, and the holdings carry the names because a count does not let anybody judge
    /// whether transferring is right.
    /// </remarks>
    [Fact]
    public async Task A_member_who_owns_something_is_refused_and_the_holdings_name_it()
    {
        PostgresMemberDirectory members = await ReadyAsync();

        Guid owner = await MakeAsync(members, "holder", "publisher");

        await OwnAsync(owner, service: "their_service", folder: "their_folder");

        MemberHoldings holdings =
            (await members.HoldingsOfAsync("holder", CancellationToken.None))!.Value;

        Assert.True(holdings.Any);
        Assert.Equal(TheirService, holdings.Services);
        Assert.Equal(TheirFolder, holdings.Folders);

        // Named in the sentence the refusal carries, both of them.
        Assert.Contains("their_service", holdings.Explanation, StringComparison.Ordinal);
        Assert.Contains("their_folder", holdings.Explanation, StringComparison.Ordinal);

        Assert.Equal(
            MemberRemoval.HoldsThings,
            await members.RemoveAsync("holder", CancellationToken.None));

        // And they are still there. A refusal that removed half of what it refused to remove is
        // the shape D-48 was about.
        Assert.NotNull(await members.HoldingsOfAsync("holder", CancellationToken.None));
    }

    /// <summary>
    /// A folder is qualified with nothing and a service with its folder.
    /// </summary>
    /// <remarks>
    /// The endpoint splits these names back apart to delete them, so the shape matters: a service
    /// in a folder has to come back as <c>folder/name</c> or the delete disposition addresses the
    /// wrong service — or none.
    /// </remarks>
    [Fact]
    public async Task A_service_in_a_folder_is_reported_with_its_folder()
    {
        PostgresMemberDirectory members = await ReadyAsync();

        Guid owner = await MakeAsync(members, "in_folder", "publisher");

        await ExecuteAsync(
            $"insert into {SchemaName}.service (id, name, folder, kind, owner_principal_id, "
            + "sharing, status) values (gen_random_uuid(), 'inside', 'shelf', 'FeatureServer', "
            + $"'{owner}', 'private', 'started')");

        MemberHoldings holdings =
            (await members.HoldingsOfAsync("in_folder", CancellationToken.None))!.Value;

        Assert.Equal(InsideShelf, holdings.Services);
    }

    /// <summary>
    /// Transferring moves the service, the folder, and the column nothing reads.
    /// </summary>
    /// <remarks>
    /// <b>The vestigial <c>layer.owner_principal_id</c> moves too.</b> Nothing has read it since
    /// migration 11 (D-33), and it is written anyway: a stale principal id in a column somebody may
    /// one day read is how the next D-24 starts, and it costs one statement in a transaction that
    /// is open regardless.
    /// </remarks>
    [Fact]
    public async Task Transferring_moves_everything_including_the_column_nothing_reads()
    {
        PostgresMemberDirectory members = await ReadyAsync();

        Guid giver = await MakeAsync(members, "giver", "publisher");
        await MakeAsync(members, "taker", "publisher");

        await OwnAsync(giver, service: "moving", folder: "moving_folder");

        // The layer has to sit in a service: `layer.service_id` is `not null`, which is migration
        // 11's whole point — a layer belongs to a container.
        object? host = await ScalarObjectAsync(
            $"select id from {SchemaName}.service where name = 'moving'");

        // A layer row carrying the dead column, so the transfer can be seen to move it. It needs a
        // data source, because `layer.data_source_id` is `not null` — the row is a fixture rather
        // than a publication, so the source is a bare one with no credential in it.
        await ExecuteAsync(
            $"insert into {SchemaName}.data_source (id, name, kind, connection_secret, key_version) "
            + "values ('11111111-1111-1111-1111-111111111111', 'fixture', 'postgis', "
            + "decode('00', 'hex'), 1)");

        await ExecuteAsync(
            $"insert into {SchemaName}.layer (id, data_source_id, name, schema_name, table_name, "
            + "geometry_column, identity_column, srid, geometry_type, is_hosted, "
            + "owner_principal_id, service_id, layer_index) values (gen_random_uuid(), "
            + "'11111111-1111-1111-1111-111111111111', 'a_layer', 'hosted', 't', 'geom', "
            + $"'objectid', 4326, 'Polygon', false, '{giver}', '{host}', 0)");

        Assert.Equal(
            MemberRemoval.Removed,
            await members.TransferAndRemoveAsync("giver", "taker", CancellationToken.None));

        MemberHoldings taken =
            (await members.HoldingsOfAsync("taker", CancellationToken.None))!.Value;

        Assert.Equal(Moving, taken.Services);
        Assert.Equal(MovingFolder, taken.Folders);

        Assert.Null(await members.HoldingsOfAsync("giver", CancellationToken.None));

        // Nothing is left pointing at the principal that went. D-66: neither live owner column has
        // a foreign key, so nothing in the schema would have caught it.
        Assert.Equal(0L, await ScalarAsync(
            $"select count(*) from {SchemaName}.layer where owner_principal_id = '{giver}'"));
    }

    /// <summary>
    /// A transfer to somebody who is not there, or who cannot sign in, changes nothing.
    /// </summary>
    /// <remarks>
    /// <b>Transferring to a disabled account produces content nobody can administer</b>, which is
    /// a worse outcome than the refusal — and the two refusals are separate because the operator's
    /// next move differs: create the member, or enable them.
    /// </remarks>
    [Fact]
    public async Task A_transfer_to_an_absent_or_disabled_member_changes_nothing()
    {
        PostgresMemberDirectory members = await ReadyAsync();

        Guid giver = await MakeAsync(members, "still_here", "publisher");
        await MakeAsync(members, "sleeping", "publisher");
        await OwnAsync(giver, service: "untouched", folder: null);

        Assert.Equal(
            MemberRemoval.TargetAbsent,
            await members.TransferAndRemoveAsync("still_here", "nobody", CancellationToken.None));

        await members.SetDisabledAsync("sleeping", true, CancellationToken.None);

        Assert.Equal(
            MemberRemoval.TargetDisabled,
            await members.TransferAndRemoveAsync("still_here", "sleeping", CancellationToken.None));

        // Both refusals left the member and the service exactly where they were.
        MemberHoldings holdings =
            (await members.HoldingsOfAsync("still_here", CancellationToken.None))!.Value;

        Assert.Equal(Untouched, holdings.Services);
    }

    /// <summary>
    /// The only administrator who can still sign in is refused, whichever disposition is asked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This branch is unreachable through the API today, and that is why it is tested here.</b>
    /// The endpoint refuses self-removal first, and the last administrator is necessarily the one
    /// making the request — so an administrator cannot reach it. It becomes reachable the moment
    /// something else holds <c>admin:manageMembers</c>: another role, an api key, or an automation.
    /// A guard that only fires in a configuration nobody has yet is a guard nothing would notice
    /// breaking, so the port is where it gets proved.
    /// </para>
    /// <para>
    /// <b>Disabled administrators do not count.</b> A server whose only administrator cannot sign
    /// in is already locked out (D-14), and treating that account as cover for removing the working
    /// one would produce exactly the state the check exists to prevent.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_last_administrator_who_can_sign_in_is_refused()
    {
        PostgresMemberDirectory members = await ReadyAsync();

        await MakeAsync(members, "only_admin", "administrator");
        await MakeAsync(members, "spare_admin", "administrator");

        // Two who can sign in: either may go.
        Assert.Equal(
            MemberRemoval.Removed,
            await members.RemoveAsync("spare_admin", CancellationToken.None));

        // One left: refused, and refused before anything else is considered.
        Assert.Equal(
            MemberRemoval.LastAdministrator,
            await members.RemoveAsync("only_admin", CancellationToken.None));

        await MakeAsync(members, "asleep_admin", "administrator");
        await members.SetDisabledAsync("asleep_admin", true, CancellationToken.None);

        Assert.Equal(
            MemberRemoval.LastAdministrator,
            await members.RemoveAsync("only_admin", CancellationToken.None));

        // And a transfer does not get around it: the objection is to losing the administrator, not
        // to what they own.
        await MakeAsync(members, "a_publisher", "publisher");

        Assert.Equal(
            MemberRemoval.LastAdministrator,
            await members.TransferAndRemoveAsync(
                "only_admin", "a_publisher", CancellationToken.None));
    }

    /// <summary>Removing somebody who is not there says so.</summary>
    [Fact]
    public async Task A_member_who_is_not_there_is_reported_absent()
    {
        PostgresMemberDirectory members = await ReadyAsync();

        Assert.Equal(
            MemberRemoval.Absent,
            await members.RemoveAsync("never_existed", CancellationToken.None));

        Assert.Null(await members.HoldingsOfAsync("never_existed", CancellationToken.None));
    }

    /// <summary>
    /// Groups are reported as zero because there is no table, and that is on purpose.
    /// </summary>
    /// <remarks>
    /// The owner asked for the disposition to cover groups — *"şu anda grubumuz yok ama olacak"* —
    /// so the field is in the shape now. A caller reading it today keeps working on the day groups
    /// arrive, which is the difference between an addition and a change.
    /// </remarks>
    [Fact]
    public async Task Groups_are_reported_as_none_rather_than_omitted()
    {
        PostgresMemberDirectory members = await ReadyAsync();
        await MakeAsync(members, "someone", "publisher");

        MemberHoldings holdings =
            (await members.HoldingsOfAsync("someone", CancellationToken.None))!.Value;

        Assert.Equal(0, holdings.Groups);
    }

    private async Task<PostgresMemberDirectory> ReadyAsync()
    {
        await MigrateAsync();
        return new PostgresMemberDirectory(DataSource);
    }

    private static async Task<Guid> MakeAsync(
        PostgresMemberDirectory members, string name, string role)
    {
        // <b>A hash that never authenticates anybody.</b> These tests are about ownership, not
        // about signing in — and a real derivation would make every one of them pay for Argon2,
        // which is deliberately expensive.
        //
        // <b>The parameters have to be JSON, though.</b> `local_credential.parameters` is a `json`
        // column and the hasher writes `_current.ToJson()` into it, so a plausible-looking
        // `m=1,t=1,p=1` answered `22P02: invalid input syntax for type json` on every one of these
        // — the database refusing a shape, which is the right place for it to be refused.
        Principal? made = await members.CreateMemberAsync(
            name,
            name,
            new PasswordHash("argon2id", """{"m":1,"t":1,"p":1}""", [1, 2, 3]),
            role,
            "creator",
            CancellationToken.None);

        Assert.NotNull(made);
        return made.Id;
    }

    /// <summary>Gives a principal a service and, optionally, a folder.</summary>
    private async Task OwnAsync(Guid owner, string service, string? folder)
    {
        await ExecuteAsync(
            $"insert into {SchemaName}.service (id, name, kind, owner_principal_id, sharing, "
            + $"status) values (gen_random_uuid(), '{service}', 'FeatureServer', '{owner}', "
            + "'private', 'started')");

        if (folder is not null)
        {
            await ExecuteAsync(
                $"insert into {SchemaName}.folder (name, owner_principal_id) "
                + $"values ('{folder}', '{owner}')");
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarObjectAsync(string sql)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
