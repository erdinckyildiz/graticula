using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Graticula.Features;
using Xunit;

namespace Graticula.Architecture.Tests;

/// <summary>
/// Enforces <c>docs/adr/ADR-008-query-engine.md</c> §4.1 — the query model
/// carries no SQL concepts — with the one exception §4a-i records, and no others.
/// </summary>
/// <remarks>
/// <para>
/// <b>This test exists because the rule had already been broken twice and nobody
/// noticed either.</b> §4.1 says the model "must contain no SQL concepts. The
/// moment it does, it stops being able to target GeoParquet, FlatGeobuf, or
/// anything that is not a database." The two breaches were not equivalent:
/// </para>
/// <para>
/// <c>FeatureQuery.Where</c> is a <see cref="ParsedWhere"/>, which carries SQL
/// text — but text <see cref="PredicateSql"/> emitted, from a parsed expression,
/// with every literal bound. It is a deliberate, argued exception (ADR-008 §4a)
/// and it is allowed here by name.
/// </para>
/// <para>
/// <c>FeatureQuery.Having</c> was a raw <c>string?</c> holding the caller's own
/// SQL, appended to the statement unparsed by <c>PostGisFeatureSource</c>. That
/// was an injection reachable by anyone who could query a layer shared to public,
/// and it survived review because a comment claimed it received "the same
/// treatment as <c>where</c>" — which was false, and cheap to believe. It is gone
/// (D-41), and this test is what makes its return a build failure instead of a
/// finding.
/// </para>
/// <para>
/// <b>The check is deliberately blunt.</b> "Contains no SQL concepts" is not
/// decidable, so this does not try: it pins the one permitted SQL-shaped type to
/// the one place it may appear, and refuses any public member whose name or type
/// advertises SQL. A rule that can only be enforced by judgement is a rule that
/// erodes; the shape of both breaches above is that judgement was applied once,
/// in a comment, and never again.
/// </para>
/// </remarks>
public sealed class SqlStaysOutOfTheQueryModelTests
{
    /// <summary>
    /// The single member permitted to carry SQL into the query model, as
    /// <c>DeclaringType.Name</c>, and the reason it is permitted.
    /// </summary>
    /// <remarks>
    /// One entry. Adding a second is a change to ADR-008 §4.1 and must be argued
    /// there before it is added here — the point of an allow-list this small is
    /// that growing it is visible in review.
    /// </remarks>
    private static readonly Dictionary<string, string> Permitted = new(StringComparer.Ordinal)
    {
        ["FeatureQuery.Where"] =
            "ADR-008 §4a: WhereClause parses the caller's predicate and re-emits our own SQL "
            + "with every literal bound, so what crosses is our text and not theirs.",

        // Q-109, 2026-10-17: the having clause crosses the same way, and is what D-41's refusal waited for.
        ["FeatureQuery.set_Having(value)"] = "The init accessor of FeatureQuery.Having, below; ADR-008 §4a-iii.",
        ["FeatureQuery.Having"] =
            "Q-109: HavingClause replaces each aggregate with a name of its own, parses the rest with the where "
            + "grammar and re-emits our own SQL with every literal bound under @h; the aggregates are rebuilt from "
            + "its own table, so what crosses is our text and not theirs.",

        // The ArcGIS gap review, 2026-10-17: a statistic's SQL expression crosses the same way (ADR-008 §4a-iv).
        ["StatisticRequest.Expression"] =
            "ADR-008 §4a-iv: StatisticExpression hands onStatisticField to the where grammar as one IS NULL test, "
            + "keeps only its operand and re-emits it through PredicateSql with every literal bound under @s.",
        ["StatisticRequest.set_Expression(value)"] = "The init accessor of StatisticRequest.Expression, above.",
        ["StatisticRequest.Deconstruct(Expression)"] = "The record's deconstruction of StatisticRequest.Expression, above.",

        // D-301: a grouped expression crosses the same way, under the same section.
        ["FeatureQuery.GroupExpressions"] = "ADR-008 §4a-iv: each a GroupExpression built from StatisticExpression's BoundExpression.",
        ["FeatureQuery.set_GroupExpressions(value)"] = "The init accessor of FeatureQuery.GroupExpressions, above.",
    };

    private static Assembly CoreAssembly => typeof(FeatureQuery).Assembly;

    /// <summary>
    /// The namespace the query model lives in, and the boundary of this check.
    /// </summary>
    /// <remarks>
    /// <b>Scoped rather than assembly-wide, after the first run said so.</b> Run
    /// against all of Core it flagged <c>QueryTrace.SqlMicroseconds</c>, which is a
    /// <c>long</c> of elapsed time in <c>Graticula.Diagnostics</c> — named for SQL
    /// because it measures time spent in it, carrying none. Widening the
    /// allow-list to admit it would have taught the next reader that entries there
    /// are noise. The rule is about the query model, so the check is too.
    /// </remarks>
    private const string QueryModel = "Graticula.Features";

    [Fact]
    public void No_unpermitted_member_of_the_query_model_carries_sql()
    {
        List<string> offenders = [];

        foreach (Type type in CoreAssembly.GetExportedTypes())
        {
            if (!string.Equals(type.Namespace, QueryModel, StringComparison.Ordinal))
            {
                continue;
            }

            // <b>The parser, the emitter and their result are the sanctioned
            // boundary, not instances of the problem.</b> ParsedWhere exists to
            // carry SQL, and these two exist to produce it — their `out
            // ParsedWhere` is where the one permitted exception is manufactured.
            // Everything downstream of them is what this test is for.
            //
            // <b>PredicateSql joined this list on 2026-08-19 and it is not a
            // widening.</b> WhereClause used to parse and emit in one pass; ADR-039
            // §5 split the emitting half out so a second front end — Filter
            // Encoding 2.0, over WFS — has one target instead of writing SQL of its
            // own. The manufacture moved, it did not multiply. What would be a
            // widening is a second entry in <see cref="Permitted"/>, and that list
            // is still one long.
            //
            // <b>SqlDialect joined it on 2026-09-23 for the same reason.</b> It is how the
            // emitter spells a statement per datastore — a placeholder, an integer division,
            // which functions want `numeric` — and PredicateSql took a placeholder function
            // for exactly that until ADR-083 brought three more differences. Nothing in the
            // model holds one; the provider hands it to the emitter at the boundary.
            //
            // <b>HavingClause joined it on 2026-10-17 (ADR-008 §4a-iii)</b>: it manufactures the having clause's
            // ParsedWhere the way WhereClause manufactures where's, through the same grammar and emitter.
            //
            // <b>StatisticExpression and its BoundExpression joined it on 2026-10-18 (ADR-008 §4a-iv)</b>, the same way.
            if (type == typeof(ParsedWhere)
                || type == typeof(BoundExpression)
                || type == typeof(GroupExpression)
                || type == typeof(StatisticExpression)
                || type == typeof(WhereClause)
                || type == typeof(HavingClause)
                || type == typeof(PredicateSql)
                || type == typeof(SqlDialect))
            {
                continue;
            }

            foreach (PropertyInfo property in type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Check($"{type.Name}.{property.Name}", property.Name, property.PropertyType);
            }

            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Check(
                        $"{type.Name}.{method.Name}({parameter.Name})",
                        parameter.Name ?? string.Empty,
                        parameter.ParameterType);
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"""
             A member of the query model carries SQL, and ADR-008 §4.1 forbids it:

                 {string.Join("\n    ", offenders)}

             §4.1: the model "must contain no SQL concepts. The moment it does, it stops being
             able to target GeoParquet, FlatGeobuf, or anything that is not a database."

             There are three permitted exceptions, FeatureQuery.Where, FeatureQuery.Having and
             StatisticRequest.Expression, and §4a-i, §4a-iii and §4a-iv explain why. If this member is a second exception, amend ADR-008 first and add
             it to Permitted with its argument — do not widen the allow-list to make a build
             pass. If it is a caller's SQL, it is D-41 happening again: parse it and bind the
             literals, as WhereClause does.
             """);

        void Check(string where, string name, Type type)
        {
            if (Permitted.ContainsKey(where))
            {
                return;
            }

            Type target = type.IsByRef || type.IsArray || type.IsPointer
                ? type.GetElementType() ?? type
                : type;

            target = Nullable.GetUnderlyingType(target) ?? target;

            if (target == typeof(ParsedWhere) || target == typeof(BoundExpression))
            {
                offenders.Add($"{where} is a {target.Name}, which carries SQL");
                return;
            }

            // A member named for SQL is one whether or not its type says so —
            // Having was a plain string.
            if (name.Contains("sql", StringComparison.OrdinalIgnoreCase)
                || where.EndsWith(".Having", StringComparison.Ordinal))
            {
                offenders.Add($"{where} is named for SQL and its type is {target.Name}");
            }
        }
    }

    [Fact]
    public void FeatureQuery_refuses_a_having_clause()
    {
        // <b>The regression test for D-41, kept here rather than with the query
        // parameter tests.</b> Those cover the HTTP boundary; this covers the
        // constructor, which is the door an internal caller would use. Both were
        // needed: the boundary is where the injection arrived, and the constructor
        // is where it could return without anybody editing an endpoint.
        ArgumentException refused = Assert.Throws<ArgumentException>(
            () => new FeatureQuery(
                limit: 10,
                statistics: [new StatisticRequest(StatisticKind.Count, "id", "n")],
                having: "count(id) > 0 and (select current_user) = 'gis'"));

        Assert.Equal("having", refused.ParamName);
        Assert.Contains("cannot be carried as SQL text", refused.Message, StringComparison.Ordinal);
    }
}
