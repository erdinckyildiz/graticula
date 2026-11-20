using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Graticula.Report.Pdf;

/// <summary>
/// What Word tolerates in a document and OfficeIMO refuses, made into what Word does with it before the engine sees it —
/// ADR-302 condition 1.
/// </summary>
/// <remarks>
/// <para>
/// Two of thirteen public Survey123 templates were refused by OfficeIMO 3.5.3 (ADR-302 §4), each for something Word lays out
/// without a word. The filled <c>.docx</c> the person downloads is not touched: only the copy this process lays out.
/// </para>
/// <list type="bullet">
/// <item><b>A footer further from the page's edge than the bottom margin</b> — <i>"PDF footer offset must not exceed the
/// bottom margin when footer content is enabled"</i>. Word pushes the body up until the footer fits (its <i>Page Setup</i>
/// has always allowed the two numbers to be set apart). Here the section's bottom margin is raised to the footer's distance
/// plus a line for each of the footer's paragraphs, which is where Word's body ends for a footer of plain lines. A footer
/// holding a picture or a table may be taller than that; the approximation is said, not hidden.</item>
/// <item><b>Two bookmarks of one name</b> — <i>"PDF bookmark names must be unique"</i>. A merged report repeats its
/// template once per record, so a template with a bookmark in it (Word's own <c>_GoBack</c>, a <c>_Hlk…</c> it left behind)
/// holds it once per record. Word keeps the first and ignores the rest for every cross-reference; here the later ones are
/// renamed with a suffix, so the first is the one a field or hyperlink still reaches.</item>
/// </list>
/// </remarks>
internal static class WordTolerances
{
    /// <summary>One line of footer text, in twentieths of a point: 12 pt at Word's 1.15 spacing, rounded up.</summary>
    internal const int FooterLine = 280;

    /// <summary>What was changed, for the answer's log line.</summary>
    internal sealed record Changes(int SectionsRaised, int BookmarksRenamed)
    {
        public bool Any => SectionsRaised + BookmarksRenamed > 0;
    }

    /// <summary>The document at <paramref name="path"/>, made tolerable, as a stream the engine can load.</summary>
    public static (MemoryStream Document, Changes Changed) Prepare(string path)
    {
        MemoryStream copy = new();

        using (FileStream read = File.OpenRead(path))
        {
            read.CopyTo(copy);
        }

        copy.Position = 0;
        Changes changed;

        using (WordprocessingDocument document = WordprocessingDocument.Open(copy, isEditable: true))
        {
            MainDocumentPart main = document.MainDocumentPart
                ?? throw new InvalidDataException("The document has no main part, so it is not a Word document.");
            changed = new Changes(RaiseBottomMargins(main), RenameRepeatedBookmarks(main));
        }

        copy.Position = 0;
        return (copy, changed);
    }

    /// <summary>Each section whose footer distance exceeds its bottom margin gets the margin Word would give it.</summary>
    internal static int RaiseBottomMargins(MainDocumentPart main)
    {
        int raised = 0;

        foreach (SectionProperties section in main.Document?.Descendants<SectionProperties>() ?? [])
        {
            if (section.GetFirstChild<PageMargin>() is not { } margin || margin.Footer?.Value is not { } footer)
            {
                continue;
            }

            int bottom = margin.Bottom?.Value ?? 0;

            if (footer <= Math.Abs(bottom))
            {
                continue;
            }

            int lines = Math.Max(1, section.Elements<FooterReference>()
                .Select(r => r.Id?.Value is { } id && main.GetPartById(id) is FooterPart part
                    ? part.Footer?.Elements<Paragraph>().Count() ?? 0
                    : 0)
                .DefaultIfEmpty(0)
                .Max());

            // A negative bottom margin is Word's "exactly": the body may run under the footer. Its sign is kept.
            int wanted = checked((int)footer + (lines * FooterLine));
            margin.Bottom = bottom < 0 ? -wanted : wanted;
            raised++;
        }

        return raised;
    }

    /// <summary>Every bookmark after the first of its name is renamed <c>name_2</c>, <c>name_3</c> …, in the body and its parts.</summary>
    internal static int RenameRepeatedBookmarks(MainDocumentPart main)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        Dictionary<string, int> repeats = new(StringComparer.Ordinal);
        int renamed = 0;

        IEnumerable<OpenXmlPartRootElement?> roots =
            new OpenXmlPartRootElement?[] { main.Document }
                .Concat(main.HeaderParts.Select(p => (OpenXmlPartRootElement?)p.Header))
                .Concat(main.FooterParts.Select(p => (OpenXmlPartRootElement?)p.Footer))
                .Concat(main.FootnotesPart is { } notes ? [notes.Footnotes] : [])
                .Concat(main.EndnotesPart is { } ends ? [ends.Endnotes] : []);

        foreach (OpenXmlPartRootElement? root in roots)
        {
            foreach (BookmarkStart start in root?.Descendants<BookmarkStart>() ?? [])
            {
                if (start.Name?.Value is not { } name)
                {
                    continue;
                }

                if (seen.Add(name))
                {
                    continue;
                }

                int next = repeats.TryGetValue(name, out int count) ? count + 1 : 2;
                string candidate = $"{name}_{next}";

                while (!seen.Add(candidate))
                {
                    next++;
                    candidate = $"{name}_{next}";
                }

                repeats[name] = next;
                start.Name = candidate;
                renamed++;
            }
        }

        return renamed;
    }
}
