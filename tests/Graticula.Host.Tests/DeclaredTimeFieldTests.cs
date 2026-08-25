using System;
using System.Collections.Generic;
using Graticula.Api.Wms;
using Graticula.Features;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// Which column is a layer's time, when the schema has more than one date.
/// </summary>
/// <remarks>
/// <para>
/// <b>[Q-129](../../docs/open-questions.md), and the row named both failures.</b> A
/// table with <c>created_at</c> and <c>observed_at</c> published no time dimension at
/// all — honest and useless. A table with only <c>created_at</c> published one over
/// the wrong column, which is worse: an animation of when rows were inserted looks
/// exactly like an animation of when things happened, and nothing anywhere says which
/// it is.
/// </para>
/// <para>
/// <b>The declaration wins and is still checked.</b> A registered table's schema
/// drifts under us (A-023), so the column somebody named last month may be gone or may
/// no longer be a date. The tests below pin both halves, because *honours a
/// declaration* and *survives a declaration that has stopped being true* are different
/// promises and only the first is obvious.
/// </para>
/// </remarks>
public sealed class DeclaredTimeFieldTests
{
    private static readonly IReadOnlyList<FieldDescription> TwoDates =
    [
        new FieldDescription("objectid", FieldType.Integer, false, null),
        new FieldDescription("created_at", FieldType.Date, true, null),
        new FieldDescription("observed_at", FieldType.Date, true, null),
    ];

    private static readonly IReadOnlyList<FieldDescription> OneDate =
    [
        new FieldDescription("objectid", FieldType.Integer, false, null),
        new FieldDescription("created_at", FieldType.Date, true, null),
    ];

    [Fact]
    public void Two_dates_and_no_declaration_is_still_no_dimension()
    {
        // The behaviour Q-129 was opened against, kept: two answers is no answer, and
        // picking the first would filter maps by whichever column the provider happened
        // to list first.
        Assert.Null(TimeDimension.FieldOf(TwoDates));
    }

    [Fact]
    public void A_declaration_picks_the_column_out_of_two()
    {
        Assert.Equal("observed_at", TimeDimension.FieldOf(TwoDates, "observed_at"));
        Assert.Equal("created_at", TimeDimension.FieldOf(TwoDates, "created_at"));
    }

    [Fact]
    public void A_declaration_is_matched_the_way_a_person_types_it()
    {
        // PostgreSQL folds unquoted identifiers to lower case, so a table created with
        // `ObservedAt` unquoted has a column called `observedat` — and the publisher
        // typing what they wrote in the DDL has to find it.
        Assert.Equal("observed_at", TimeDimension.FieldOf(TwoDates, "OBSERVED_AT"));
        Assert.Equal("observed_at", TimeDimension.FieldOf(TwoDates, "Observed_At"));

        // <b>Case, not punctuation.</b> `ObservedAt` is a different name from
        // `observed_at` however it is folded, and matching them would be this server
        // guessing at a column nobody named.
        Assert.Null(TimeDimension.FieldOf(TwoDates, "ObservedAt"));

        // And the answer is the column's own spelling rather than the publisher's: it
        // goes into a SQL predicate, where the two are not interchangeable.
        Assert.Equal("observed_at", TimeDimension.FieldOf(TwoDates, "OBSERVED_AT"));
    }

    [Fact]
    public void A_declaration_that_no_longer_holds_falls_back_rather_than_failing()
    {
        // <b>The A-023 case.</b> A registered table dropped the column, or renamed it.
        // Answering nothing would take a working layer off the air; answering the
        // derived column is what the layer had before anybody declared anything.
        Assert.Equal("created_at", TimeDimension.FieldOf(OneDate, "observed_at"));
        Assert.Null(TimeDimension.FieldOf(TwoDates, "no_such_column"));
    }

    [Fact]
    public void A_declaration_naming_a_column_that_is_not_a_date_does_not_hold()
    {
        // A publisher who names the key column gets the derivation, not a time
        // dimension over an integer.
        Assert.Equal("created_at", TimeDimension.FieldOf(OneDate, "objectid"));
        Assert.False(TimeDimension.DeclarationHolds(OneDate, "objectid"));
    }

    [Fact]
    public void Declaring_nothing_holds_and_declaring_something_present_holds()
    {
        // The control on DeclarationHolds itself: it is what the console shows, and a
        // check that answered false for the ordinary case would put a warning on every
        // layer page in the product.
        Assert.True(TimeDimension.DeclarationHolds(TwoDates, null));
        Assert.True(TimeDimension.DeclarationHolds(TwoDates, ""));
        Assert.True(TimeDimension.DeclarationHolds(TwoDates, "observed_at"));
        Assert.False(TimeDimension.DeclarationHolds(TwoDates, "gone_at"));
    }

    [Fact]
    public void One_date_and_no_declaration_is_the_behaviour_that_did_not_change()
    {
        // <b>The control on the whole feature.</b> Every layer in every deployment has
        // nothing declared, so the derivation is what almost every request still uses.
        Assert.Equal("created_at", TimeDimension.FieldOf(OneDate));
        Assert.Equal("created_at", TimeDimension.FieldOf(OneDate, null));
    }
}
