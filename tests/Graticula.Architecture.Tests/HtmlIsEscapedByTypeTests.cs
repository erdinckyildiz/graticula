using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Graticula.Architecture.Tests;

/// <summary>
/// ADR-023 condition 3: HTML is escaped by a type, and the files that still escape by hand are named.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the type does not stop on its own.</b> <c>Html</c> escapes every hole that is not already markup,
/// and <c>RestDirectory.Wrap</c> takes nothing else, so a page built for the directory cannot carry a raw
/// value without saying <c>Html.Markup</c>. A file that builds its own page and calls an encoder by hand is
/// outside that, and nothing would notice a new one — which is how the surface grew from four document kinds
/// to fifteen files with one helper and a rule.
/// </para>
/// <para>
/// <b>So the hand-encoders are counted, and the list only shrinks.</b> Each entry says why it is not on the
/// type yet. A new file that encodes HTML by hand fails here with the sentence to read; moving one onto the
/// type removes its line.
/// </para>
/// </remarks>
public sealed class HtmlIsEscapedByTypeTests
{
    private static readonly Dictionary<string, string> Permitted = new(StringComparer.Ordinal)
    {
        ["src/Graticula.Host/Html.cs"] = "The type itself: the one place WebUtility.HtmlEncode is called for a page.",
        ["src/Graticula.Host/OAuthEndpoints.cs"] =
            "The OAuth sign-in pages, their own renderer with System.Text.Encodings.Web's encoder. Not moved on "
            + "2026-10-12 because that encoder writes non-ASCII as numeric references and WebUtility does not, and "
            + "ArcGIS clients read these pages' title — a byte change nothing local can check.",
        ["src/Graticula.Api.Wms/FeatureInfoWriter.cs"] =
            "WMS GetFeatureInfo's text/html answer, in a project the host's type is not visible from.",
    };

    private static readonly string[] Encoders = ["WebUtility.HtmlEncode", "HttpUtility.HtmlEncode", "HtmlEncoder."];

    [Fact]
    public void Only_the_named_files_encode_html_by_hand()
    {
        DirectoryInfo root = FindRepositoryRoot();
        string src = Path.Combine(root.FullName, "src");

        List<string> found = [];

        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root.FullName, file).Replace('\\', '/');

            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(file);

            if (Encoders.Any(e => text.Contains(e, StringComparison.Ordinal)) && !Permitted.ContainsKey(relative))
            {
                found.Add(relative);
            }
        }

        Assert.True(
            found.Count == 0,
            "These files encode HTML by hand: " + string.Join(", ", found) + ". Build the page as Html "
            + "(Html.Of, HtmlBuilder) and hand it to RestDirectory.Wrap — a string hole is escaped for you and "
            + "Html.Markup names the ones that are not (ADR-023 condition 3). If the file has its own renderer for "
            + "a reason, add it to HtmlIsEscapedByTypeTests.Permitted with that reason.");
    }

    [Fact]
    public void Every_permitted_file_still_exists_and_still_needs_its_line()
    {
        DirectoryInfo root = FindRepositoryRoot();

        foreach (string relative in Permitted.Keys)
        {
            string path = Path.Combine(root.FullName, relative);

            Assert.True(File.Exists(path), $"{relative} is permitted to encode HTML by hand and no longer exists; remove its line.");

            string text = File.ReadAllText(path);

            Assert.True(
                Encoders.Any(e => text.Contains(e, StringComparison.Ordinal)),
                $"{relative} no longer encodes HTML by hand; remove its line from Permitted, so the list says what is true.");
        }
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!;
    }
}
