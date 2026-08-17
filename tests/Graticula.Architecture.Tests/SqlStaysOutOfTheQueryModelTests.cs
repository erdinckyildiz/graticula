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
/// text — but text <see cref="WhereClause"/> emitted, from a parsed expression,
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

            // <b>The parser and its result are the sanctioned boundary, not
            // instances of the problem.</b> ParsedWhere exists to carry SQL, and
            // WhereClause exists to produce it — its `out ParsedWhere` is where
            // the one permitted exception is manufactured. Everything downstream
            // of them is what this test is for.
            if (type == typeof(ParsedWhere) || type == typeof(WhereClause))
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

             There is exactly one permitted exception, FeatureQuery.Where, and §4a-i explains
             why it is one. If this member is a second exception, amend ADR-008 first and add
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

            if (target == typeof(ParsedWhere))
            {
                offenders.Add($"{where} is a ParsedWhere, which carries SQL");
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
