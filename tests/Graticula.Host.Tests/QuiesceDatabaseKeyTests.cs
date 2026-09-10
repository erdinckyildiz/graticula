using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A quiesce is keyed on the database it reaches, not on how somebody typed the address.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-251](../../docs/architecture-debt.md).</b>
/// [ADR-059](../../docs/adr/ADR-059-quiescing-a-data-source.md) §5d says the unit is the
/// database — *two registered data sources may point at one database. Quiescing either takes
/// both out* — and until 2026-09-10 the unit was the connection string, so the two agreed only
/// when the strings matched byte for byte.
/// </para>
/// <para>
/// <b>Measured on the console fixture before this was written.</b> `datastore` is
/// <c>Host=localhost</c> and `ci_second_source` is <c>Host=127.0.0.1</c> against one PostgreSQL.
/// Quiescing the datastore answered <c>alsoQuiesced: []</c>, refused its own layer with 503, and
/// served the other source's layer out of the same database — 60 rows, from a database a DBA had
/// been told was out of service.
/// </para>
/// <para>
/// <b>These are unit facts about the key, and the behaviour they stand for is asserted over HTTP
/// by <c>QuiesceControlTests</c>.</b> The key is where the defect was, and a key is cheap to
/// pin from every direction: the shapes that must fold together, and the ones that must not.
/// </para>
/// </remarks>
public sealed class QuiesceDatabaseKeyTests
{
    /// <summary>Two spellings of this machine are one database.</summary>
    [Theory]
    [InlineData("Host=localhost;Port=55432;Database=gis", "Host=127.0.0.1;Port=55432;Database=gis")]
    [InlineData("Host=LOCALHOST;Port=55432;Database=gis", "Host=::1;Port=55432;Database=gis")]
    [InlineData("Host=localhost;Port=55432;Database=GIS", "Host=localhost;Port=55432;Database=gis")]
    public void Two_spellings_of_one_database_are_one_key(string one, string other) =>
        Assert.Equal(SourceQuiesce.DatabaseKey(one), SourceQuiesce.DatabaseKey(other));

    /// <summary>
    /// The credential is not part of the identity, and that reverses an earlier argument.
    /// </summary>
    /// <remarks>
    /// <b>The listing that computed sharing said the opposite</b> — that two sources differing
    /// only in their credential *would look shared and would not be*. That reasons from the
    /// pool, and a pool is not what a DBA takes out of service: two connections to one database
    /// block one another whoever they signed in as, which is the whole subject of ADR-059.
    /// </remarks>
    [Fact]
    public void The_credential_is_not_part_of_the_database()
    {
        Assert.Equal(
            SourceQuiesce.DatabaseKey("Host=db;Port=5432;Database=gis;Username=a;Password=x"),
            SourceQuiesce.DatabaseKey("Host=db;Port=5432;Database=gis;Username=b;Password=y"));

        // <b>And neither is anything else the pool cares about.</b> Ordering, spacing and
        // pooling parameters all changed the old key and none of them changes the database.
        Assert.Equal(
            SourceQuiesce.DatabaseKey("Database=gis; Host=db ;Port=5432"),
            SourceQuiesce.DatabaseKey(
                "Host=db;Port=5432;Database=gis;Maximum Pool Size=40;Application Name=x"));
    }

    /// <summary>Different databases stay different, which is the half a fold can break.</summary>
    [Theory]
    [InlineData("Host=db;Port=5432;Database=gis", "Host=db;Port=5432;Database=other")]
    [InlineData("Host=db;Port=5432;Database=gis", "Host=db;Port=5433;Database=gis")]
    [InlineData("Host=one;Port=5432;Database=gis", "Host=two;Port=5432;Database=gis")]
    public void Two_databases_stay_two(string one, string other) =>
        Assert.NotEqual(SourceQuiesce.DatabaseKey(one), SourceQuiesce.DatabaseKey(other));

    /// <summary>
    /// A remote host under two names is still two keys, and that is recorded rather than fixed.
    /// </summary>
    /// <remarks>
    /// <b>Asserted so the limit cannot be lost.</b> Telling two DNS names for one host apart
    /// means resolving them inside a request — a lookup per pair, wrong for a host with several
    /// addresses and wrong again for a name that resolves differently inside a container. D-251
    /// carries the decision; this makes the current answer explicit rather than accidental, so
    /// that anybody who changes it has to change a test that says why.
    /// </remarks>
    [Fact]
    public void A_remote_host_under_two_names_is_not_reconciled() =>
        Assert.NotEqual(
            SourceQuiesce.DatabaseKey("Host=db.example.test;Port=5432;Database=gis"),
            SourceQuiesce.DatabaseKey("Host=10.0.0.7;Port=5432;Database=gis"));

    /// <summary>
    /// A connection string this cannot read keeps its own identity rather than joining a crowd.
    /// </summary>
    /// <remarks>
    /// <b>The failure mode a normalisation invites.</b> Collapsing every unparseable string onto
    /// one empty key would put every such source under one hold, so quiescing one would take out
    /// all of them — a worse answer than the defect being repaired.
    /// </remarks>
    [Fact]
    public void An_unreadable_connection_string_is_its_own_key()
    {
        Assert.NotEqual(
            SourceQuiesce.DatabaseKey("nonsense"),
            SourceQuiesce.DatabaseKey("also nonsense"));

        Assert.Equal(string.Empty, SourceQuiesce.DatabaseKey("   "));
    }

    /// <summary>
    /// Normalising a normalised key changes nothing, which <c>Current</c> relies on.
    /// </summary>
    /// <remarks>
    /// <b>Relied on rather than obvious.</b> <c>SourceQuiesce.Current</c> walks its own keys and
    /// passes each back through <c>Holding</c>, which normalises again. It works because a key
    /// this produces has no <c>=</c> in it and an unparseable string is returned unchanged — two
    /// facts in different halves of one method. Asserted here, because *it happens to work* is
    /// how a thing stops working.
    /// </remarks>
    [Fact]
    public void Normalising_a_key_again_changes_nothing()
    {
        string once = SourceQuiesce.DatabaseKey("Host=127.0.0.1;Port=55432;Database=Gis");

        Assert.Equal(once, SourceQuiesce.DatabaseKey(once));
    }

    /// <summary>The port is assumed when it is not written, because PostgreSQL assumes it.</summary>
    [Fact]
    public void An_unwritten_port_is_the_default_one() =>
        Assert.Equal(
            SourceQuiesce.DatabaseKey("Host=db;Database=gis"),
            SourceQuiesce.DatabaseKey("Host=db;Port=5432;Database=gis"));
}
