using System;
using System.Collections.Generic;
using System.IO;
using Graticula.Host;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A password that is already published is refused, decorations and all.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-23](../../docs/architecture-debt.md): nothing checked a password against known-breached
/// lists.</b> Length was the only rule, so <c>Passw0rd!</c> and a random nine characters were
/// treated alike — and the first is in every wordlist. The row's own note on why: Pwned Passwords
/// is about 10 GB, and the k-anonymity API leaks a hash prefix to a third party, and neither fits
/// a product whose baseline is one deployable against one PostgreSQL.
/// </para>
/// <para>
/// <b>So this is not a breach corpus and the tests say so.</b> What is under test is the shape of
/// the defence — normalise, then compare — because that is what turns a few hundred entries into
/// coverage of the decorations people actually add.
/// </para>
/// </remarks>
public sealed class CommonPasswordTests
{
    private static readonly IReadOnlySet<string> Known = CommonPasswords.Load(null);

    /// <summary>
    /// The obvious ones, and the row's own example.
    /// </summary>
    /// <remarks>
    /// <b><c>Passw0rd!</c> is the case the row names</b>, and it is the one a literal wordlist
    /// misses: it is what people type when a form demands a capital, a digit and a symbol.
    /// </remarks>
    [Theory]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("Passw0rd!")]
    [InlineData("P@ssw0rd")]
    [InlineData("password123")]
    [InlineData("Password2024")]
    [InlineData("p@ssword!!")]
    [InlineData("12345678")]
    [InlineData("qwertyuiop")]
    [InlineData("letmein!")]
    [InlineData("iloveyou2")]
    [InlineData("Sifre123")]
    [InlineData("galatasaray")]
    [InlineData("postgres")]
    [InlineData("Graticula1")]
    public void A_published_password_is_refused(string chosen)
    {
        Assert.True(
            CommonPasswords.Known(chosen, Known),
            $"'{chosen}' was accepted. That is D-23: the realistic attack on a small deployment "
            + "is credential stuffing, and Argon2id and the rate limit do nothing at all about a "
            + "password that is already published.");
    }

    /// <summary>
    /// A password nobody published is accepted.
    /// </summary>
    /// <remarks>
    /// <b>The half that stops this being a rule that refuses everything.</b> The normalisation
    /// undoes leet substitutions, and undoing too many of them would turn strong random secrets
    /// into dictionary words by accident. These are the check that it does not.
    /// </remarks>
    [Theory]
    [InlineData("correct horse battery staple")]
    [InlineData("kirmizi-fener-sekiz")]
    [InlineData("Tq7xLm2vRp")]
    [InlineData("hedgerow-flint-ambulance")]
    [InlineData("yamalik bayir 1993")]
    public void A_password_nobody_published_is_accepted(string chosen)
    {
        Assert.False(
            CommonPasswords.Known(chosen, Known),
            $"'{chosen}' was refused. A rule that refuses strong passwords is a rule people route "
            + "around, which is the reasoning the length rule was already lowered under.");
    }

    /// <summary>
    /// A password with no letters or digits at all has nothing in it.
    /// </summary>
    /// <remarks>
    /// <b>An edge the normalisation creates.</b> <c>!!!!!!!!</c> reduces to an empty string, and
    /// an empty string is not in the list — so without this clause it would pass a rule aimed at
    /// exactly that kind of password.
    /// </remarks>
    [Fact]
    public void A_password_that_reduces_to_nothing_is_refused()
    {
        Assert.True(CommonPasswords.Known("!!!!!!!!", Known));
        Assert.True(CommonPasswords.Known("........", Known));
    }

    /// <summary>
    /// A deployment can supply its own list, and it is added to the built-in one.
    /// </summary>
    /// <remarks>
    /// <b>The mechanism is the durable part.</b> The built-in few hundred are the floor; a
    /// deployment that keeps a breach corpus points at it and gets the real thing, without this
    /// server carrying ten gigabytes or asking a third party.
    /// </remarks>
    [Fact]
    public void A_deployments_own_list_is_added_to_the_built_in_one()
    {
        string file = Path.Combine(Path.GetTempPath(), $"zz-common-{Guid.NewGuid():N}.txt");

        File.WriteAllLines(file, ["yamalik-bayir", "kirmizi fener"]);

        try
        {
            IReadOnlySet<string> extended = CommonPasswords.Load(file);

            Assert.True(CommonPasswords.Known("Yamalik-Bayir!", extended));
            Assert.True(CommonPasswords.Known("password", extended));
            Assert.False(CommonPasswords.Known("Yamalik-Bayir!", Known));
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// A named list that is not there stops the server.
    /// </summary>
    /// <remarks>
    /// <b>Because the silent fallback is the failure this row is about.</b> A deployment that
    /// pointed at a breach corpus and quietly got a few hundred entries would believe it had a
    /// defence it does not have.
    /// </remarks>
    [Fact]
    public void A_named_list_that_is_not_there_is_refused()
    {
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
            () => CommonPasswords.Load(Path.Combine(Path.GetTempPath(), "zz-nothing-here.txt")));

        Assert.Contains("CommonPasswordFile", refused.Message, StringComparison.Ordinal);
    }
}
