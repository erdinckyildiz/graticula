using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using HarfBuzzSharp;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Graticula.Render.Skia;

/// <summary>
/// One font of the label stack, as the raster face draws from it.
/// </summary>
/// <remarks>
/// <b>The same list, in the same order, as the tile face's glyph ranges —
/// [ADR-100](../../docs/adr/ADR-100-labels-in-more-scripts.md).</b> Both read
/// <c>tools/fonts/stack.json</c>, so a WMS map and a vector tile of the same layer take a
/// codepoint from the same font, and there is one place to change which one that is.
/// </remarks>
internal sealed class LabelFace
{
    private readonly (int First, int Last)[] _claims;
    private readonly object _shaping = new();
    private SKShaper? _shaper;

    internal LabelFace(SKTypeface typeface, string file, (int First, int Last)[] claims)
    {
        Typeface = typeface;
        File = file;
        _claims = claims;
    }

    /// <summary>The face.</summary>
    internal SKTypeface Typeface { get; }

    /// <summary>The file it was read from, for a test and for a log line.</summary>
    internal string File { get; }

    /// <summary>Whether this face has a glyph for the codepoint.</summary>
    internal bool Has(int codePoint) => Typeface.GetGlyph(codePoint) != 0;

    /// <summary>Whether the codepoint is in a block this face answers first for.</summary>
    internal bool Claims(int codePoint)
    {
        foreach ((int first, int last) in _claims)
        {
            if (codePoint >= first && codePoint <= last)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Shapes one run of text in this face.</summary>
    /// <remarks>
    /// <b>One shaper per face for the process, and a lock around it.</b> Building one reads the
    /// whole font into HarfBuzz, which is the cost a map drawn through many canvases must not pay
    /// per canvas; whether HarfBuzz's font object may be shaped from two threads at once is not
    /// something SkiaSharp documents, and shaping a label is microseconds, so the lock costs
    /// nothing worth measuring and removes the question.
    /// </remarks>
    internal SKShaper.Result Shape(string text, SKFont font, bool leftToRight)
    {
        using HarfBuzzSharp.Buffer buffer = new();

        buffer.AddUtf16(text);
        buffer.GuessSegmentProperties();

        // A run of digits is laid out left to right whatever script its digits belong to —
        // Arabic-Indic digits are written in the Arabic script and still read left to right,
        // and HarfBuzz would otherwise guess right-to-left from the script and reverse them.
        if (leftToRight)
        {
            buffer.Direction = Direction.LeftToRight;
        }

        lock (_shaping)
        {
            _shaper ??= new SKShaper(Typeface);

            return _shaper.Shape(buffer, font);
        }
    }
}

/// <summary>
/// The faces the raster renderer draws labels from, and how a label is split among them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Before [ADR-100](../../docs/adr/ADR-100-labels-in-more-scripts.md) this was one face,
/// DejaVu Sans, and a label it could not draw went to the machine's font manager</b> — which on
/// the air-gapped image this product is built for has nothing to offer, so an Arabic, Hindi or
/// Thai label was named in the log and drawn as boxes. The stack is now the tile face's:
/// DejaVu, Noto Sans, and a Noto family per script, embedded in this assembly; and Noto Sans CJK
/// when an image was built with it, read from the <c>fonts</c> directory beside the server.
/// </para>
/// <para>
/// <b>Shaped, which the tile face's clients do not all do.</b> Skia draws glyphs and does not
/// choose them: without a shaper an Arabic word is a row of isolated letters left to right, and a
/// Devanagari conjunct is its parts. HarfBuzz chooses them, so the raster face draws those scripts
/// correctly — better than MapLibre draws the same label from glyph ranges, which ADR-100 §5 says
/// in so many words rather than claiming the two faces look the same.
/// </para>
/// <para>
/// <b>Not the Unicode bidirectional algorithm.</b> A label is split into runs of one face each,
/// and when its first strong letter is Arabic or Hebrew the runs are laid out right to left. That
/// is right for a label in one right-to-left script, with or without numbers in it, and for one
/// Latin word inside it; it is not the full algorithm, and a label that nests several changes of
/// direction can come out in the wrong order. Recorded in ADR-100 rather than discovered.
/// </para>
/// </remarks>
internal static class LabelFaces
{
    /// <summary>A run of a label, in one face.</summary>
    internal readonly record struct Run(LabelFace Face, string Text, bool Digits);

    private static readonly Lazy<List<LabelFace>> Loaded = new(Load);

    /// <summary>The stack, in order. Empty only when not one face could be read.</summary>
    internal static IReadOnlyList<LabelFace> Stack => Loaded.Value;

    /// <summary>The fonts directory beside the server, where an optional face is looked for.</summary>
    internal static string Directory => Path.Combine(AppContext.BaseDirectory, "fonts");

    /// <summary>
    /// The face this codepoint is drawn from: the first that claims its block and has it, else
    /// the first that has it — the rule <c>tools/make-glyphs.py</c> applies to the glyph ranges.
    /// </summary>
    /// <returns>The face, or null when none in the stack has the codepoint.</returns>
    internal static LabelFace? For(int codePoint)
    {
        IReadOnlyList<LabelFace> stack = Stack;

        foreach (LabelFace face in stack)
        {
            if (face.Claims(codePoint) && face.Has(codePoint))
            {
                return face;
            }
        }

        foreach (LabelFace face in stack)
        {
            if (face.Has(codePoint))
            {
                return face;
            }
        }

        return null;
    }

    /// <summary>
    /// Splits a label into runs of one face, in reading order.
    /// </summary>
    /// <param name="text">The label.</param>
    /// <param name="fallback">
    /// The face for a codepoint no face in the stack has — the machine's, found by the caller, or
    /// the first of the stack so that it is at least drawn as the box it is.
    /// </param>
    /// <returns>The runs, in logical order.</returns>
    /// <remarks>
    /// <b>Spaces, punctuation and marks stay with the run they are in</b> when its face has them.
    /// Taken literally, the first-font rule would draw the space in an Arabic street name from
    /// DejaVu, which has it first, and break one right-to-left run into three — and then the words
    /// come out in the wrong order. <b>Digits are the exception, and start a run of their own</b>,
    /// because a number reads left to right inside a right-to-left label.
    /// </remarks>
    internal static List<Run> Segment(string text, Func<int, LabelFace> fallback)
    {
        List<Run> runs = [];
        LabelFace? current = null;
        bool digits = false;
        int start = 0;

        for (int i = 0; i < text.Length;)
        {
            int codePoint = char.ConvertToUtf32(text, i);
            int width = char.IsSurrogatePair(text, i) ? 2 : 1;
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(codePoint);
            bool digit = category == UnicodeCategory.DecimalDigitNumber;

            if (current is not null && !digit && !digits && Neutral(category) && current.Has(codePoint))
            {
                i += width;
                continue;
            }

            if (current is not null && digit && digits && current.Has(codePoint))
            {
                i += width;
                continue;
            }

            LabelFace face = For(codePoint) ?? fallback(codePoint);

            if (current is null)
            {
                current = face;
                digits = digit;
            }
            else if (!ReferenceEquals(face, current) || digit != digits)
            {
                runs.Add(new Run(current, text[start..i], digits));
                current = face;
                digits = digit;
                start = i;
            }

            i += width;
        }

        if (current is not null && start < text.Length)
        {
            runs.Add(new Run(current, text[start..], digits));
        }

        return runs;
    }

    /// <summary>Whether a label's first strong letter is written right to left.</summary>
    internal static bool RightToLeft(string text)
    {
        for (int i = 0; i < text.Length;)
        {
            int codePoint = char.ConvertToUtf32(text, i);
            i += char.IsSurrogatePair(text, i) ? 2 : 1;

            if (codePoint is (>= 0x0590 and <= 0x08FF) or (>= 0xFB1D and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFF))
            {
                return true;
            }

            if (char.IsLetter(char.ConvertFromUtf32(codePoint), 0))
            {
                return false;
            }
        }

        return false;
    }

    private static bool Neutral(UnicodeCategory category) => category
        is UnicodeCategory.SpaceSeparator
        or UnicodeCategory.ConnectorPunctuation
        or UnicodeCategory.DashPunctuation
        or UnicodeCategory.OpenPunctuation
        or UnicodeCategory.ClosePunctuation
        or UnicodeCategory.InitialQuotePunctuation
        or UnicodeCategory.FinalQuotePunctuation
        or UnicodeCategory.OtherPunctuation
        or UnicodeCategory.NonSpacingMark
        or UnicodeCategory.EnclosingMark
        or UnicodeCategory.SpacingCombiningMark
        or UnicodeCategory.Format;

    /// <summary>
    /// Reads the stack: the embedded faces in <c>stack.json</c>'s order, an optional one from the
    /// fonts directory when it is there, and then any other face a deployment put in that directory.
    /// </summary>
    /// <remarks>
    /// <b>A face that cannot be read is left out rather than failing the renderer</b>, for the reason
    /// the single bundled face fell back rather than throwing: a server that refuses to draw any map
    /// is worse than one that draws most labels, and a codepoint no face has still reaches the
    /// caller's report, so the gap stays loud.
    /// </remarks>
    private static List<LabelFace> Load()
    {
        List<LabelFace> stack = [];
        HashSet<string> named = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            using Stream? manifest = Resource("stack.json");

            if (manifest is null)
            {
                return stack;
            }

            using JsonDocument document = JsonDocument.Parse(manifest);

            foreach (JsonElement font in document.RootElement.GetProperty("fonts").EnumerateArray())
            {
                string file = font.GetProperty("file").GetString() ?? string.Empty;
                bool optional = font.TryGetProperty("optional", out JsonElement o) && o.ValueKind == JsonValueKind.True;

                named.Add(file);

                SKTypeface? typeface = optional ? FromDirectory(file) : FromResource(file);

                if (typeface is not null)
                {
                    stack.Add(new LabelFace(typeface, file, Claims(font)));
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException
                                     or InvalidOperationException or FormatException or NotSupportedException)
        {
            return stack;
        }

        // <b>And whatever a deployment put in the directory — D-161's mount.</b> Last, with no
        // claims, so a mounted face can add a script and cannot take one away from the stack.
        try
        {
            if (System.IO.Directory.Exists(Directory))
            {
                foreach (string path in System.IO.Directory.EnumerateFiles(Directory)
                             .Where(static p => p.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                                                || p.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
                             .Order(StringComparer.Ordinal))
                {
                    if (named.Contains(Path.GetFileName(path)))
                    {
                        continue;
                    }

                    if (SKTypeface.FromFile(path) is { } mounted)
                    {
                        stack.Add(new LabelFace(mounted, Path.GetFileName(path), []));
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A directory that cannot be listed is a directory with nothing in it.
        }

        return stack;
    }

    private static (int First, int Last)[] Claims(JsonElement font) =>
    [
        .. font.GetProperty("claims").EnumerateArray().Select(static block =>
        {
            string[] ends = (block.GetString() ?? string.Empty).Split('-');

            return (int.Parse(ends[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    int.Parse(ends[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }),
    ];

    private static Stream? Resource(string file) =>
        typeof(LabelFaces).Assembly.GetManifestResourceStream("Graticula.Render.Skia.fonts." + file);

    private static SKTypeface? FromResource(string file)
    {
        using Stream? resource = Resource(file);

        return resource is null ? null : SKTypeface.FromStream(resource);
    }

    private static SKTypeface? FromDirectory(string file)
    {
        string path = Path.Combine(Directory, file);

        return File.Exists(path) ? SKTypeface.FromFile(path) : null;
    }
}
