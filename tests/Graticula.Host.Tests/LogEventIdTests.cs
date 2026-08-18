using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// Every log message has an event id of its own, and says something in it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written because three ids named seven messages between them.</b> Found 2026-08-19 while reading
/// <c>Log.cs</c> for the next free number: <c>1008</c> was both
/// <c>AuthorizationIsPortalShaped</c> and <c>OverlayKilled</c>, <c>1010</c> was
/// <c>SetupStillPending</c> and <c>OverlayWorkersReclaimed</c>, and <c>1014</c> was a datastore
/// refusal, a query's timings and a configuration warning. Nothing had gone wrong yet, which is the
/// point: an id is what an operator filters on when they cannot read every line, and one that returns
/// three unrelated things is worse than no id at all, because the number reads as an identity.
/// </para>
/// <para>
/// <b>The same disease this repository already caught in a register.</b> The debt numbers collided and
/// it took three entries before anybody noticed, after which the counting moved into a tool. The
/// lesson recorded then was that a numbering convention nobody checks is a numbering convention — so
/// this is the check, and the comment at the top of <c>Log.cs</c> points at it rather than restating
/// the rule.
/// </para>
/// <para>
/// <b>By reflection rather than by reading the file.</b> A regular expression over source would pass
/// on a file that does not compile and would miss a message declared anywhere else. The attribute
/// survives on the generated method, so this asks the assembly what it actually ships.
/// </para>
/// </remarks>
public sealed class LogEventIdTests
{
    /// <summary>
    /// Every declared message, with the id and template it carries.
    /// </summary>
    /// <remarks>
    /// <b>Found by attribute, not by class.</b> <c>Log</c> is where they all live today and naming it
    /// would make this test stop covering the second such class somebody adds — which is D-74's shape,
    /// a set of values with no one place that names them all.
    /// </remarks>
    private static List<(string Method, LoggerMessageAttribute Message)> Declared()
    {
        List<(string Method, LoggerMessageAttribute Message)> found = [];

        foreach (Type type in typeof(Program).Assembly.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                LoggerMessageAttribute? message = method.GetCustomAttribute<LoggerMessageAttribute>();

                if (message is not null)
                {
                    found.Add(($"{type.Name}.{method.Name}", message));
                }
            }
        }

        return found;
    }

    [Fact]
    public void No_two_messages_share_an_event_id()
    {
        List<(string Method, LoggerMessageAttribute Message)> declared = Declared();

        // <b>A guard on the guard.</b> If reflection stops finding them — a renamed attribute, a
        // trimmed assembly, a generator that stops emitting it — this test would pass while asserting
        // nothing, which is the failure mode `NativeDependencyTests` was written a second arm to avoid.
        Assert.True(
            declared.Count >= 15,
            $"Only {declared.Count} logger messages were found by reflection, and Log.cs declares "
            + "more than twenty. This test is no longer looking at what it thinks it is.");

        string[] collisions = declared
            .GroupBy(entry => entry.Message.EventId)
            .Where(static group => group.Count() > 1)
            .Select(group => $"{group.Key} is used by {string.Join(", ", group.Select(e => e.Method))}")
            .ToArray();

        Assert.True(
            collisions.Length == 0,
            $"""
             Log event ids are not unique:

                 {string.Join("\n    ", collisions)}

             An operator filters on an id when they cannot read every line, so an id that returns two
             unrelated messages is worse than none. Give the newer declaration the next free number —
             the comment at the top of Log.cs says which that is — and leave the older one where it is,
             so a filter that already works keeps working.
             """);
    }

    /// <summary>
    /// A message is prose, or it is one parameter carrying the prose. Never a label.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not style policing.</b> These messages are the whole of what an operator gets at 2 AM, and
    /// §67's scenarios are graded on whether the log says what to do next. A template short enough to
    /// be a label cannot.
    /// </para>
    /// <para>
    /// <b>The exception is real, and this test was wrong without it.</b> The first version asked only
    /// for length and flagged <c>SchemaIncompatible</c> and <c>SchemaCompatible</c>, whose entire
    /// template is <c>{Explanation}</c> — the sentence is composed by the schema comparison, the only
    /// thing that knows which version met which, and handed over whole. That is a deliberate shape
    /// rather than a short message, so the rule is *prose, or a single placeholder*. Written down
    /// because a rule that needs an exception on its first run is worth explaining rather than quietly
    /// widening.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_message_is_prose_or_a_parameter_carrying_it()
    {
        string[] thin = Declared()
            .Where(static entry =>
            {
                string template = (entry.Message.Message ?? string.Empty).Trim();

                // One placeholder and nothing else: the message is built elsewhere and handed over.
                if (template.StartsWith('{')
                    && template.EndsWith('}')
                    && template.IndexOf('{', 1) < 0)
                {
                    return false;
                }

                return template.Length < 25;
            })
            .Select(entry => $"{entry.Method}: \"{entry.Message.Message}\"")
            .ToArray();

        Assert.True(
            thin.Length == 0,
            $"""
             A log message is too short to tell anybody anything:

                 {string.Join("\n    ", thin)}

             The 2 AM scenarios in §67 are graded on whether the log says what to do next. If the
             sentence is genuinely composed elsewhere, make the template exactly one placeholder and
             nothing else — that shape is allowed and this rule knows about it.
             """);
    }
}
