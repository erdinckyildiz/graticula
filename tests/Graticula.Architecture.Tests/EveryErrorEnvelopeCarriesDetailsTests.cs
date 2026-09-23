using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Graticula.Architecture.Tests;

/// <summary>
/// That every error envelope the server writes carries <c>details</c> beside <c>code</c> and <c>message</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>V-60, the third ArcGIS review.</b> ArcGIS's envelope is always <c>{"error":{"code","message","details":[]}}</c>
/// and client code written against it reads <c>error.details.join(", ")</c>. This server wrote that shape from
/// some faces and <c>{code, message}</c> from others — 53 places in all, because each face spelt its own
/// envelope — so a client's error path worked or threw depending on which operation had refused.
/// </para>
/// <para>
/// <b>An enumerating check, because the shape is written in so many places</b> that the next face will spell it
/// again (D-46). An object with <c>description</c> instead of <c>message</c> is left alone: that is the error
/// inside an <c>applyEdits</c> or <c>addAttachment</c> result, and ArcGIS spells it that way.
/// </para>
/// </remarks>
public sealed class EveryErrorEnvelopeCarriesDetailsTests
{
    private static readonly Regex Opening = new(@"\berror\s*=\s*new\s*\{", RegexOptions.Compiled);

    private static readonly Regex Message = new(@"\bmessage\b", RegexOptions.Compiled);

    private static readonly Regex Description = new(@"\bdescription\s*=", RegexOptions.Compiled);

    private static DirectoryInfo Root()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!;
    }

    private static IEnumerable<(string Path, string Text)> Sources()
    {
        string src = Path.Combine(Root().FullName, "src");

        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (Path.GetRelativePath(Root().FullName, file), File.ReadAllText(file));
        }
    }

    /// <summary>The body of the object that opens at <paramref name="open"/>, skipping string literals.</summary>
    private static string Body(string text, int open)
    {
        int depth = 0;

        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '"')
            {
                for (i++; i < text.Length && text[i] != '"'; i++)
                {
                    if (text[i] == '\\')
                    {
                        i++;
                    }
                }

                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && --depth == 0)
            {
                return text[(open + 1)..i];
            }
        }

        return text[(open + 1)..];
    }

    [Fact]
    public void Every_error_with_a_message_names_its_details()
    {
        List<string> bare = [];

        foreach ((string path, string text) in Sources())
        {
            foreach (Match opening in Opening.Matches(text))
            {
                string body = Body(text, opening.Index + opening.Length - 1);

                if (Message.IsMatch(body) && !Description.IsMatch(body) && !body.Contains("details", StringComparison.Ordinal))
                {
                    int line = text.AsSpan(0, opening.Index).Count('\n') + 1;
                    bare.Add($"{path}:{line}");
                }
            }
        }

        Assert.True(
            bare.Count == 0,
            "An error envelope without `details`, which an ArcGIS client reads as an array: "
            + string.Join(", ", bare.Take(20)));
    }
}
