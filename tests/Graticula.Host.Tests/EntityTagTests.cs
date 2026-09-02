using Graticula.Api.OgcFeatures;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// Reading the <c>If-Match</c> header a conditional write arrives with.
/// </summary>
/// <remarks>
/// <para>
/// <b>The half of [D-186](../../docs/architecture-debt.md) that is pure text handling, tested
/// where it is pure.</b> The rest of optimistic concurrency needs a database and two writers;
/// this needs neither, and it is the part most likely to be quietly wrong — a parser that
/// returns nothing for every header would make every conditional write behave exactly like an
/// unconditional one, which no round-trip test notices because the writes still succeed.
/// </para>
/// <para>
/// <b>Absent versus unusable is the assertion that matters.</b> They differ by one status code
/// and by whether a client's edit is protected at all.
/// </para>
/// </remarks>
public sealed class EntityTagTests
{
    [Fact]
    public void No_header_is_no_precondition()
    {
        EntityTags.Precondition read = EntityTags.Read(null);

        Assert.False(read.Present);
        Assert.False(read.Unusable);
        Assert.False(read.Any);
        Assert.Empty(read.Versions);
    }

    [Fact]
    public void A_quoted_tag_gives_up_its_version()
    {
        EntityTags.Precondition read = EntityTags.Read("\"1234\"");

        Assert.True(read.Present);
        Assert.False(read.Unusable);
        Assert.Equal(["1234"], read.Versions);
    }

    [Fact]
    public void A_list_keeps_every_tag_because_any_of_them_may_match()
    {
        // RFC 9110 §13.1.1: the precondition holds if *any* member matches. Keeping only the
        // first would refuse a request the specification says must succeed.
        EntityTags.Precondition read = EntityTags.Read("\"1\", \"2\" ,\"3\"");

        Assert.Equal(["1", "2", "3"], read.Versions);
    }

    [Fact]
    public void A_star_asks_only_that_the_feature_exist()
    {
        EntityTags.Precondition read = EntityTags.Read("*");

        Assert.True(read.Present);
        Assert.True(read.Any);
        Assert.Empty(read.Versions);
        Assert.False(read.Unusable);
    }

    [Fact]
    public void A_weak_tag_alone_is_unusable_rather_than_absent()
    {
        // <b>The distinction the whole type exists for.</b> A weak tag can never satisfy
        // If-Match, so there is nothing to compare — and reporting *no precondition* would
        // apply the edit unconditionally and answer 204 to a client that believes it was
        // protected.
        EntityTags.Precondition read = EntityTags.Read("W/\"1234\"");

        Assert.True(read.Present);
        Assert.True(read.Unusable);
        Assert.Empty(read.Versions);
    }

    [Fact]
    public void A_strong_tag_beside_a_weak_one_still_gives_something_to_compare()
    {
        EntityTags.Precondition read = EntityTags.Read("W/\"1\", \"2\"");

        Assert.False(read.Unusable);
        Assert.Equal(["2"], read.Versions);
    }

    [Theory]
    [InlineData("1234")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"unterminated")]
    public void A_header_with_nothing_comparable_in_it_is_unusable(string header)
    {
        EntityTags.Precondition read = EntityTags.Read(header);

        Assert.True(read.Present);
        Assert.True(read.Unusable);
    }

    [Fact]
    public void What_is_written_is_what_can_be_read_back()
    {
        // The round trip is the property that matters: a tag this server emits has to parse
        // back to the version it was made from, or every conditional write it invites fails.
        EntityTags.Precondition read = EntityTags.Read(EntityTags.For("98765"));

        Assert.Equal(["98765"], read.Versions);
    }

    [Fact]
    public void A_version_containing_a_quote_cannot_break_out_of_the_tag()
    {
        // xmin is digits, so this cannot happen today — which is exactly why it is worth
        // pinning: the day a source reports something else, an unescaped quote would produce
        // a header that parses as two tags, and the second would be nonsense.
        Assert.Equal("\"12ab34\"", EntityTags.For("12\"ab\"34"));
    }
}
