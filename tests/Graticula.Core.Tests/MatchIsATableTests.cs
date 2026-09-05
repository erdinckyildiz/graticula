using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Graticula.Cartography;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// A `match` answers from a table, and answers exactly what the scan it replaced would have.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by removing a limit rather than by reading the code.</b> A classification cost a
/// walk of every class for every feature, and that is affordable at the 256 classes a stored
/// document used to be capped at. [ADR-054](../../docs/adr/ADR-054-the-symbology-document-is-not-bounded.md)
/// withdrew the cap, the owner's own layer classified into 1,177, and drawing it took three
/// times as long as a layer with <b>twice the features and no classification</b> — 1.9 s against
/// 0.65 s for one 1600×800 picture of Turkey.
/// </para>
/// <para>
/// <b>Speed is the reason and sameness is the risk</b>, so most of this file is about sameness.
/// `StyleExpression.Same` compares two doubles as numbers and everything else as invariant text,
/// and those two rules disagree about `-0.0`; a table cannot hold a comparison that is not
/// transitive, so a match carrying a zero keeps the scan. That refusal is asserted here, because
/// an optimisation that is *nearly* the same answer is worse than none.
/// </para>
/// </remarks>
public sealed class MatchIsATableTests
{
    /// <summary>Evaluates an expression against one attribute.</summary>
    /// <param name="expression">The style expression, as JSON.</param>
    /// <param name="column">The column the match reads.</param>
    /// <param name="value">Its value on this feature.</param>
    /// <returns>What the expression evaluated to.</returns>
    private static object? Read(string expression, string column, object? value)
    {
        Dictionary<string, object?> attributes = new() { [column] = value };

        return StyleExpression.Compile(JsonNode.Parse(expression)).Evaluate(
            new StyleExpression.Context(attributes, 10.0));
    }

    /// <summary>
    /// The table finds a label, misses one that is absent, and keeps the first of two.
    /// </summary>
    [Fact]
    public void A_match_on_text_answers_what_the_scan_answered()
    {
        const string Style =
            """["match",["get","ad"],"Bodrum","#111111","Urla","#222222","Bodrum","#333333","#000000"]""";

        Assert.Equal("#111111", Read(Style, "ad", "Bodrum"));
        Assert.Equal("#222222", Read(Style, "ad", "Urla"));

        // <b>The first of two, which is what walking the list gave.</b> A document with the same
        // label twice is a merge of two classifications or a hand edit, and the answer must not
        // change because the lookup happens to be keyed.
        Assert.Equal("#111111", Read(Style, "ad", "Bodrum"));

        Assert.Equal("#000000", Read(Style, "ad", "Kandıra"));
        Assert.Equal("#000000", Read(Style, "ad", null));
    }

    /// <summary>
    /// A number and the text of that number are the same label, in both directions.
    /// </summary>
    /// <remarks>
    /// <b>`Same` falls to invariant text unless both sides are doubles.</b> So a feature holding
    /// the number 7 matches a label written as `"7"`, and a feature holding the string `"7"`
    /// matches a label written as `7`. The table is keyed by that same text, and this is what
    /// says so rather than the comment above it.
    /// </remarks>
    [Fact]
    public void A_number_matches_the_text_of_itself_whichever_side_it_is_on()
    {
        Assert.Equal(
            "#aa0000",
            Read("""["match",["get","kod"],"7","#aa0000","#000000"]""", "kod", 7.0));

        Assert.Equal(
            "#00aa00",
            Read("""["match",["get","kod"],7,"#00aa00","#000000"]""", "kod", "7"));

        Assert.Equal(
            "#000000",
            Read("""["match",["get","kod"],7,"#00aa00","#000000"]""", "kod", 8.0));
    }

    /// <summary>
    /// A zero label keeps the scan, so negative zero still finds it.
    /// </summary>
    /// <remarks>
    /// <b>The one place the two comparisons disagree.</b> `(-0.0).Equals(0.0)` is true and
    /// `"-0"` is not `"0"`, so `Same` says a feature holding negative zero matches a label of
    /// zero — and says the *string* `"-0"` does not. No table reproduces both. `Lookup` returns
    /// null for a match carrying a zero and the walk is kept; if somebody keys it anyway, this
    /// is the test that says which answer changed.
    /// </remarks>
    [Fact]
    public void A_zero_label_is_left_to_the_scan_and_negative_zero_still_finds_it()
    {
        const string Style = """["match",["get","n"],0,"#hit","#miss"]""";

        Assert.Equal("#hit", Read(Style, "n", 0.0));
        Assert.Equal("#hit", Read(Style, "n", -0.0));
        Assert.Equal("#miss", Read(Style, "n", 1.0));

        // And the other half of the disagreement: as text, "-0" is not "0".
        Assert.Equal("#miss", Read(Style, "n", "-0"));
    }

    /// <summary>
    /// A null label is a case the scan can hold, and it is still held.
    /// </summary>
    [Fact]
    public void A_null_label_is_left_to_the_scan_and_a_null_value_finds_it()
    {
        const string Style = """["match",["get","ad"],null,"#empty","Urla","#urla","#other"]""";

        Assert.Equal("#empty", Read(Style, "ad", null));
        Assert.Equal("#urla", Read(Style, "ad", "Urla"));
        Assert.Equal("#other", Read(Style, "ad", "Bodrum"));
    }

    /// <summary>
    /// A label list shares one output, and every value in it still finds it.
    /// </summary>
    [Fact]
    public void Several_labels_sharing_one_output_all_find_it()
    {
        const string Style =
            """["match",["get","ad"],["Bodrum","Urla","Fethiye"],"#coast","#inland"]""";

        Assert.Equal("#coast", Read(Style, "ad", "Bodrum"));
        Assert.Equal("#coast", Read(Style, "ad", "Urla"));
        Assert.Equal("#coast", Read(Style, "ad", "Fethiye"));
        Assert.Equal("#inland", Read(Style, "ad", "Ankara"));
    }

    /// <summary>
    /// A thousand classes cost about what eight do, per feature.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A ratio rather than a duration, because a duration is a claim about this machine.</b>
    /// The same number of evaluations run against a match of 8 classes and a match of 2,000, and
    /// what is asserted is that the second is not many times the first. A scan makes it about
    /// two hundred times; a table makes it about one.
    /// </para>
    /// <para>
    /// <b>Eight, because the ceiling has to leave room for a slow machine under load.</b> The
    /// measured ratio is near 1 and the failure this guards against is three orders of magnitude
    /// away, so a generous bound still catches it and does not flake in CI.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_thousand_classes_cost_about_what_eight_do()
    {
        const int Runs = 200_000;

        static string Classes(int many)
        {
            StringBuilder built = new("""["match",["get","ad"]""");

            for (int i = 0; i < many; i++)
            {
                built.Append(System.Globalization.CultureInfo.InvariantCulture,
                    $",\"place number {i}\",\"#{i % 10}{i % 10}{i % 10}{i % 10}{i % 10}{i % 10}\"");
            }

            return built.Append(""","#000000"]""").ToString();
        }

        static long Milliseconds(string style, int last)
        {
            StyleExpression compiled = StyleExpression.Compile(JsonNode.Parse(style));

            // <b>The last class, so a scan pays for the whole list.</b> This asked for class 1
            // first, which a scan finds on its second comparison — so the test passed against
            // the very walk it was written to catch. A classification of a real column is full
            // of values that are late in the list; the worst case is the honest one to measure,
            // and it is the one that made the owner's layer slow.
            Dictionary<string, object?> attributes = new()
            {
                ["ad"] = System.String.Create(
                    System.Globalization.CultureInfo.InvariantCulture, $"place number {last}"),
            };
            StyleExpression.Context context = new(attributes, 10.0);

            // Once outside the clock, so the first evaluation's own costs are not measured.
            compiled.Evaluate(context);

            Stopwatch clock = Stopwatch.StartNew();

            for (int i = 0; i < Runs; i++)
            {
                compiled.Evaluate(context);
            }

            return clock.ElapsedMilliseconds;
        }

        long few = Milliseconds(Classes(8), 7);
        long many = Milliseconds(Classes(2_000), 1_999);

        Assert.True(
            many <= (few * 8) + 50,
            $"{Runs:N0} evaluations of an 8-class match took {few} ms and the same number of a "
            + $"2,000-class match took {many} ms. A classification is being answered by walking "
            + "its classes, which is what made the owner's 1,177-class layer three times slower "
            + "to draw than one with twice the features and no classification.");
    }
}
