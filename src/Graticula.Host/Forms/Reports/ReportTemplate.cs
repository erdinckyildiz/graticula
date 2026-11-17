using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Graticula.Host.Forms.Reports;

/// <summary>Where a template's placeholder is refused, and why: the part, the paragraph's number and text, the placeholder.</summary>
/// <param name="Part"><c>document</c>, or a header's or footer's file name (<c>header1</c>).</param>
/// <param name="Paragraph">The paragraph's number in that part, counting from 1, tables' paragraphs included.</param>
/// <param name="Text">The paragraph's text, cut short.</param>
/// <param name="Placeholder">The placeholder as written, <c>${…}</c>.</param>
/// <param name="Message">Why.</param>
internal sealed record ReportIssue(string Part, int Paragraph, string Text, string Placeholder, string Message)
{
    public override string ToString() =>
        $"{(Part == "document" ? "The document" : $"The {Part}")}, paragraph {Paragraph.ToString(CultureInfo.InvariantCulture)} (\"{Text}\"), {Placeholder}: {Message}";
}

/// <summary>A file that is not a Word document this engine reads.</summary>
internal sealed class ReportTemplateException : Exception
{
    public ReportTemplateException(string message)
        : base(message)
    {
    }

    public ReportTemplateException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public ReportTemplateException()
    {
    }
}

/// <summary>
/// A feature report template: a Word <c>.docx</c> whose text holds Survey123's placeholders, read and filled by this
/// server's own engine — ADR-301. Tier 1 by the build-vs-adopt policy: a template language is the server's domain, so the
/// package is opened with <see cref="ZipArchive"/> and its parts with <see cref="System.Xml.Linq"/>, and no library.
/// </summary>
/// <remarks>
/// <para>
/// <b>Four steps, each over the main document, every header and every footer.</b> (1) Word splits what a person typed into
/// runs wherever it formats, spell-checks or tracks an edit, so <c>${name}</c> can arrive as <c>${</c>, <c>na</c>,
/// <c>me}</c>: each paragraph's text is read whole, and each placeholder's characters are moved into the run it starts in.
/// (2) Each placeholder is given a run of its own, keeping that run's formatting, so what it writes is formatted as the
/// placeholder was. (3) Each <c>${#…}</c> or <c>${if …}</c> is paired with its <c>${/}</c>, and the section is lifted to
/// the level it repeats at: inside one paragraph, the runs between; over paragraphs side by side, those paragraphs (each
/// tag standing at its paragraph's start or end); over a table's rows, those rows — Survey123's way of writing a repeat
/// as a table. (4) The part is compiled into a tree of static XML, elements, values and sections, checked against the
/// form, and filled per record.
/// </para>
/// <para>
/// <b>Nothing is dropped silently.</b> A placeholder that cannot be read, a name the form does not have, a method this
/// engine does not do, a section that does not close or sits where it cannot be repeated — each is a
/// <see cref="ReportIssue"/> with its part, paragraph and text, and a template with any is refused at upload.
/// </para>
/// </remarks>
internal sealed class ReportTemplate
{
    /// <summary>The largest template accepted, as uploaded (INFERRED; Survey123's pages give none).</summary>
    public const long MaximumBytes = 10L * 1024 * 1024;

    /// <summary>The most a template's parts may hold once unpacked, so a small file cannot unpack into a large one.</summary>
    public const long MaximumUnpackedBytes = 64L * 1024 * 1024;

    /// <summary>What the engine does not do, said with every template it checks (ADR-301 §6).</summary>
    public const string NotSupported =
        "Not supported yet: a map's rotate and mapFilters, a drawingInfo at an address, and layers of a web map at another address " +
        "(a map draws this server's own layers; a basemap from elsewhere is left out and the report says so); $inputParams; " +
        "pictures from an address or the form's media ($image | src), filtering attachments (attachmentsDefExp), EXIF " +
        "(getValue:\"x\" on a photograph). A template that uses any of them is refused with the place it is used. A PDF is the " +
        "Word document laid out by this server (ADR-302): floating pictures and text boxes may sit elsewhere than Word puts them.";

    internal static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    internal static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    internal static readonly XNamespace G = "urn:graticula:report";

    /// <summary>Marks the half of a paragraph a splice cut off before a section's end, joined to the paragraph after it.</summary>
    private static readonly XName Join = G + "join";
    private static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private static readonly XNamespace PackageRels = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string ImageRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image";
    private const string DocumentPart = "word/document.xml";

    private readonly Dictionary<string, byte[]> _entries;
    private readonly List<string> _order;
    private readonly Dictionary<string, Part> _parts = new(StringComparer.Ordinal);
    private readonly List<ReportIssue> _syntax = [];

    private ReportTemplate(Dictionary<string, byte[]> entries, List<string> order)
    {
        _entries = entries;
        _order = order;
    }

    /// <summary>The problems found reading the placeholders, before any form is asked about.</summary>
    public IReadOnlyList<ReportIssue> SyntaxIssues => _syntax;

    /// <summary>Whether any placeholder reads <c>$attachment</c>, which needs every file of a record.</summary>
    public bool ReadsAllFiles { get; private set; }

    // ------------------------------------------------------------------------------------------------------- opening

    /// <summary>Opens a <c>.docx</c> and reads its placeholders.</summary>
    /// <exception cref="ReportTemplateException">The file is not a Word document this engine reads.</exception>
    public static ReportTemplate Open(byte[] docx)
    {
        ArgumentNullException.ThrowIfNull(docx);

        if (docx.Length > MaximumBytes)
        {
            throw new ReportTemplateException($"A template is at most {MaximumBytes / (1024 * 1024)} MB, and this one is larger.");
        }

        Dictionary<string, byte[]> entries = new(StringComparer.Ordinal);
        List<string> order = [];
        long unpacked = 0;

        try
        {
            using ZipArchive zip = new(new MemoryStream(docx, writable: false), ZipArchiveMode.Read);

            if (zip.Entries.Count > 2000)
            {
                throw new ReportTemplateException("The file holds more than 2,000 parts, which no Word document does.");
            }

            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                using Stream input = entry.Open();
                using MemoryStream copy = new();
                byte[] buffer = new byte[81920];
                int read;

                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    unpacked += read;

                    if (unpacked > MaximumUnpackedBytes)
                    {
                        throw new ReportTemplateException($"The template unpacks to more than {MaximumUnpackedBytes / (1024 * 1024)} MB; a report template is a document of a few pages.");
                    }

                    copy.Write(buffer, 0, read);
                }

                entries[entry.FullName] = copy.ToArray();
                order.Add(entry.FullName);
            }
        }
        catch (InvalidDataException e)
        {
            throw new ReportTemplateException("The file is not a Word document (.docx): it is not a ZIP package.", e);
        }

        if (!entries.ContainsKey(DocumentPart) || !entries.ContainsKey("[Content_Types].xml"))
        {
            throw new ReportTemplateException("The file is not a Word document (.docx): it has no word/document.xml. A .doc, .dotx saved as another type, or an .odt is not read; save it from Word as a .docx.");
        }

        ReportTemplate template = new(entries, order);

        foreach (string name in order.Where(IsFilledPart))
        {
            template._parts[name] = template.Prepare(name);
        }

        return template;
    }

    private static bool IsFilledPart(string name) =>
        name == DocumentPart
        || (name.StartsWith("word/header", StringComparison.Ordinal) && name.EndsWith(".xml", StringComparison.Ordinal) && !name.Contains("/_rels/", StringComparison.Ordinal))
        || (name.StartsWith("word/footer", StringComparison.Ordinal) && name.EndsWith(".xml", StringComparison.Ordinal) && !name.Contains("/_rels/", StringComparison.Ordinal));

    private static string PartLabel(string name) => name == DocumentPart ? "document" : Path.GetFileNameWithoutExtension(name);

    private static XDocument Load(byte[] bytes)
    {
        XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

        try
        {
            using XmlReader reader = XmlReader.Create(new MemoryStream(bytes, writable: false), settings);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException e)
        {
            throw new ReportTemplateException($"A part of the document is not well-formed XML: {e.Message}", e);
        }
    }

    private static byte[] Save(XDocument document)
    {
        using MemoryStream output = new();
        using (XmlWriter writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
        {
            document.Save(writer);
        }

        return output.ToArray();
    }

    // --------------------------------------------------------------------------------------- preparing one part

    /// <summary>One filled part, compiled: the document with its placeholders lifted, and the tree it is filled from.</summary>
    private sealed class Part
    {
        public required string Name { get; init; }

        public required XDocument Prepared { get; init; }

        public required List<Node> Tree { get; init; }

        public required Dictionary<int, ReportTag?> Tags { get; init; }
    }

    private abstract record Node;

    private sealed record StaticNode(XNode Content) : Node;

    private sealed record ElementNode(XElement Shell, List<Node> Children) : Node;

    private sealed record ValueNode(ReportTag? Tag, XElement? RunProperties, ReportIssue Place) : Node;

    private sealed record SectionNode(ReportTag? Tag, List<Node> Body, ReportIssue Place) : Node;

    private Part Prepare(string name)
    {
        XDocument document = Load(_entries[name]);
        XElement root = document.Root ?? throw new ReportTemplateException($"{name} is empty.");
        string label = PartLabel(name);

        List<XElement> paragraphs = [.. root.Descendants(W + "p")];
        Dictionary<XElement, int> numbers = paragraphs.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i + 1);

        foreach (XElement paragraph in paragraphs)
        {
            MergeRuns(paragraph);
        }

        // Each placeholder in a run of its own, marked, and read.
        Dictionary<int, ReportTag?> tags = [];
        Dictionary<int, ReportIssue> places = [];
        int next = 0;

        foreach (XElement paragraph in paragraphs)
        {
            foreach (XElement text in OwnTexts(paragraph).ToList())
            {
                if (!HoldsTag(text.Value))
                {
                    continue;
                }

                foreach (XElement marker in Isolate(text))
                {
                    int id = next++;
                    string written = (string)marker.Attribute("text")!;
                    marker.SetAttributeValue("id", id);
                    ReportIssue place = new(label, numbers[paragraph], Excerpt(paragraph), written, string.Empty);
                    places[id] = place;

                    try
                    {
                        tags[id] = written[1] == '<' ? ReportExpression.ParseAngle(written[2..^1]) : ReportExpression.Parse(written[2..^1]);
                    }
                    catch (ReportSyntaxException e)
                    {
                        tags[id] = null;
                        _syntax.Add(place with { Message = e.Message });
                        string inner = ReportExpression.Normalise(written[2..^1]).Trim();
                        marker.SetAttributeValue("kind", inner.StartsWith('/') ? "close" : inner.StartsWith('#') || inner.StartsWith("if ", StringComparison.Ordinal) || written[1] == '<' ? "open" : "value");
                        continue;
                    }

                    marker.SetAttributeValue("kind", tags[id]!.Kind switch { ReportTagKind.Close => "close", ReportTagKind.Value => "value", _ => "open" });
                }
            }
        }

        // Pair the sections, then lift each to the level it repeats at.
        List<(XElement Open, XElement Close)> pairs = [];
        Stack<XElement> open = new();

        foreach (XElement marker in root.Descendants(G + "tag").ToList())
        {
            string kind = (string)marker.Attribute("kind")!;
            int id = (int)marker.Attribute("id")!;

            if (kind == "open")
            {
                open.Push(marker);
            }
            else if (kind == "close")
            {
                if (open.Count == 0)
                {
                    _syntax.Add(places[id] with { Message = "This ${/} closes no section: there is no ${#…} or ${if …} open before it." });
                    marker.Remove();
                    continue;
                }

                pairs.Add((open.Pop(), marker));
            }
        }

        foreach (XElement unclosed in open)
        {
            _syntax.Add(places[(int)unclosed.Attribute("id")!] with { Message = "This section is never closed: write ${/} where it ends." });
            unclosed.Remove();
        }

        foreach ((XElement o, XElement c) in pairs.OrderBy(p => (int)p.Open.Attribute("id")!))
        {
            if (Lift(o, c, tags[(int)o.Attribute("id")!]?.Kind == ReportTagKind.Summary) is { } problem)
            {
                _syntax.Add(places[(int)o.Attribute("id")!] with { Message = problem });

                // Left as static text, so the rest of the part is still checked.
                ReplaceWithText(o);
                ReplaceWithText(c);
            }
        }

        List<Node> tree = Compile([.. root.Nodes()], tags, places);
        PlaceSummaries(name, tree);
        return new Part { Name = name, Prepared = document, Tree = tree, Tags = tags };
    }

    private static bool IsSummary(Node node) => node is SectionNode { Tag.Kind: ReportTagKind.Summary };

    /// <summary>
    /// Survey123's page: summary sections stand at the start and the end of a report, never within its contents — so each
    /// is a section of the document's body itself, with only summary sections before it or only summary sections after it.
    /// </summary>
    private void PlaceSummaries(string name, List<Node> tree)
    {
        List<Node>? body = tree.OfType<ElementNode>().FirstOrDefault(n => n.Shell.Name == W + "body")?.Children;
        List<Node> content = body is null ? [] : [.. body.Where(n => !(n is StaticNode { Content: XElement e } && e.Name == W + "sectPr"))];

        foreach (SectionNode summary in Sections(tree).Where(IsSummary))
        {
            int at = content.IndexOf(summary);
            bool placed = name == DocumentPart && at >= 0
                && (content.Take(at).All(n => IsSummary(n) || IsBlank(n)) || content.Skip(at + 1).All(n => IsSummary(n) || IsBlank(n)));

            if (!placed)
            {
                _syntax.Add(summary.Place with
                {
                    Message = name != DocumentPart
                        ? "A summary section is written in the document's body, not in a header or a footer."
                        : "A summary section stands at the start or the end of the document, with its $<$summary> and $</> at the start of their own paragraphs and nothing of a record's around it: Survey123's page says it cannot be placed within the contents of a report.",
                });
            }
        }

        static IEnumerable<SectionNode> Sections(IEnumerable<Node> nodes)
        {
            foreach (Node node in nodes)
            {
                switch (node)
                {
                    case SectionNode s:
                        yield return s;

                        foreach (SectionNode inner in Sections(s.Body))
                        {
                            yield return inner;
                        }

                        break;
                    case ElementNode e:
                        foreach (SectionNode inner in Sections(e.Children))
                        {
                            yield return inner;
                        }

                        break;
                    default:
                        break;
                }
            }
        }
    }

    /// <summary>Whether a node of the body writes nothing a reader would see: an empty paragraph.</summary>
    private static bool IsBlank(Node node) =>
        node is StaticNode { Content: XElement e } ? !HasContent(e) : node is StaticNode;

    /// <summary>Whether the template has a summary section, which makes one document of all the records chosen.</summary>
    public bool HasSummary => _parts.TryGetValue(DocumentPart, out Part? document)
        && document.Tree.OfType<ElementNode>().FirstOrDefault(n => n.Shell.Name == W + "body") is { } body
        && body.Children.Any(IsSummary);

    private static string Excerpt(XElement paragraph)
    {
        string text = string.Concat(OwnTexts(paragraph).Select(t => t.Value)).Trim();
        return text.Length > 80 ? text[..77] + "…" : text;
    }

    /// <summary>The <c>w:t</c> elements of this paragraph and not of a paragraph inside it (a text box's).</summary>
    private static IEnumerable<XElement> OwnTexts(XElement paragraph) =>
        paragraph.Descendants(W + "t").Where(t => t.Ancestors(W + "p").First() == paragraph);

    /// <summary>
    /// Moves each placeholder's characters into the text element it starts in, so no placeholder is split over runs.
    /// </summary>
    private static void MergeRuns(XElement paragraph)
    {
        List<XElement> texts = [.. OwnTexts(paragraph)];

        if (texts.Count < 2)
        {
            return;
        }

        StringBuilder all = new();
        List<int> owner = [];

        for (int i = 0; i < texts.Count; i++)
        {
            all.Append(texts[i].Value);
            owner.AddRange(Enumerable.Repeat(i, texts[i].Value.Length));
        }

        string text = all.ToString();

        if (!HoldsTag(text))
        {
            return;
        }

        bool moved = false;

        foreach ((int start, int end) in Placeholders(text))
        {
            for (int k = start + 1; k < end; k++)
            {
                if (owner[k] != owner[start])
                {
                    owner[k] = owner[start];
                    moved = true;
                }
            }
        }

        if (!moved)
        {
            return;
        }

        StringBuilder[] rebuilt = [.. texts.Select(_ => new StringBuilder())];

        for (int k = 0; k < text.Length; k++)
        {
            rebuilt[owner[k]].Append(text[k]);
        }

        for (int i = 0; i < texts.Count; i++)
        {
            texts[i].Value = rebuilt[i].ToString();
            texts[i].SetAttributeValue(XNamespace.Xml + "space", "preserve");
        }
    }

    /// <summary>Where the next <c>${</c>, <c>$&lt;$summary&gt;</c> or <c>$&lt;/&gt;</c> starts, or -1.</summary>
    private static int NextTag(string text, int from)
    {
        int brace = text.IndexOf("${", from, StringComparison.Ordinal);
        int angle = from;

        while ((angle = text.IndexOf("$<", angle, StringComparison.Ordinal)) >= 0 && !IsAngleTag(text, angle))
        {
            angle += 2;
        }

        return brace < 0 ? angle : angle < 0 ? brace : Math.Min(brace, angle);
    }

    /// <summary>Whether a <c>$&lt;</c> opens one of the two angle tags, not a dollar sign before a less-than in the text.</summary>
    private static bool IsAngleTag(string text, int at) =>
        string.CompareOrdinal(text, at, "$</>", 0, 4) == 0
        || (at + 3 < text.Length && text[at + 2] == '$' && text.IndexOf('>', at) is var end && end > 0
            && text[(at + 3)..end].Trim().All(char.IsLetter) && end - at < 24);

    /// <summary>Whether a text holds a placeholder or a summary tag.</summary>
    private static bool HoldsTag(string text) => NextTag(text, 0) >= 0;

    /// <summary>Each <c>${…}</c> in a text, start and end (exclusive), reading past a <c>}</c> inside quotes.</summary>
    private static IEnumerable<(int Start, int End)> Placeholders(string text)
    {
        int i = 0;

        while ((i = NextTag(text, i)) >= 0)
        {
            // $<$summary> and $</>, Survey123's summary tags: to the next '>'.
            if (text[i + 1] == '<')
            {
                int close = text.IndexOf('>', i + 2);

                if (close < 0)
                {
                    yield break;
                }

                yield return (i, close + 1);
                i = close + 1;
                continue;
            }

            char quote = '\0';
            int j = i + 2;

            for (; j < text.Length; j++)
            {
                char c = text[j];

                if (quote != '\0')
                {
                    if (c == '\\')
                    {
                        j++;
                    }
                    else if (c == quote || (quote == '"' && c is '”' or '“'))
                    {
                        quote = '\0';
                    }
                }
                else if (c is '"' or '“' or '”')
                {
                    quote = '"';
                }
                else if (c == '}')
                {
                    break;
                }
            }

            if (j >= text.Length)
            {
                yield break;
            }

            yield return (i, j + 1);
            i = j + 1;
        }
    }

    /// <summary>
    /// Splits the run holding <paramref name="text"/> so each placeholder in it is a <c>g:tag</c> of its own, carrying the
    /// run's properties; text around placeholders stays in runs of the same formatting.
    /// </summary>
    private static List<XElement> Isolate(XElement text)
    {
        List<XElement> markers = [];
        XElement? run = text.Parent;

        if (run is null || run.Name != W + "r")
        {
            return markers;
        }

        string value = text.Value;
        List<(int Start, int End)> found = [.. Placeholders(value)];

        if (found.Count == 0)
        {
            return markers;
        }

        XElement? properties = run.Element(W + "rPr");
        List<XNode> before = [.. text.NodesBeforeSelf().Where(n => n is not XElement e || e.Name != W + "rPr")];
        List<XNode> after = [.. text.NodesAfterSelf()];
        List<XElement> replacement = [];

        XElement NewRun(IEnumerable<XNode> content) =>
            new(W + "r", run.Attributes(), properties is null ? null : new XElement(properties), content);

        XElement TextNode(string s) => new(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), s);

        if (before.Count > 0)
        {
            replacement.Add(NewRun(before.Select(Clone)));
        }

        int at = 0;

        foreach ((int start, int end) in found)
        {
            if (start > at)
            {
                replacement.Add(NewRun([TextNode(value[at..start])]));
            }

            XElement marker = new(G + "tag", new XAttribute("text", value[start..end]), properties is null ? null : new XElement(properties));
            replacement.Add(marker);
            markers.Add(marker);
            at = end;
        }

        if (at < value.Length)
        {
            replacement.Add(NewRun([TextNode(value[at..])]));
        }

        if (after.Count > 0)
        {
            replacement.Add(NewRun(after.Select(Clone)));
        }

        run.ReplaceWith(replacement);
        return markers;
    }

    private static XNode Clone(XNode node) => node switch
    {
        XElement e => new XElement(e),
        XText t => new XText(t),
        _ => node,
    };

    private static void ReplaceWithText(XElement marker)
    {
        if (marker.Parent is null)
        {
            return;
        }

        marker.ReplaceWith(new XElement(W + "r", marker.Element(W + "rPr") is { } p ? new XElement(p) : null,
            new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), (string?)marker.Attribute("text"))));
    }

    /// <summary>Whether a paragraph still holds anything a reader would see.</summary>
    private static bool HasContent(XElement paragraph) => paragraph.Descendants().Any(IsContent);

    private static bool IsContent(XElement e) =>
        (e.Name == W + "t" && e.Value.Length > 0) || e.Name == W + "drawing" || e.Name == W + "pict" || e.Name == W + "object"
        || ((e.Name == W + "tab" || e.Name == W + "sym") && e.Parent?.Name == W + "r") || e.Name.Namespace == G;

    /// <summary>Lifts a section to the level it repeats at; the reason it cannot be, or null.</summary>
    private static string? Lift(XElement open, XElement close, bool summary)
    {
        int id = (int)open.Attribute("id")!;
        XElement opening = new(G + "open", new XAttribute("id", id));
        XElement closing = new(G + "close", new XAttribute("id", id));

        // Both ends side by side — in one paragraph, or in a part of one that an outer section was spliced into.
        if (open.Parent is not null && open.Parent == close.Parent && open.Parent.Name != W + "body")
        {
            open.ReplaceWith(opening);
            close.ReplaceWith(closing);
            return null;
        }

        XElement? po = open.Ancestors(W + "p").FirstOrDefault();
        XElement? pc = close.Ancestors(W + "p").FirstOrDefault();

        if (po is null || pc is null)
        {
            return "The placeholder is not inside a paragraph, or it is inside a part of a paragraph a section around it already splits: put this section's ends in paragraphs of their own.";
        }

        if (po == pc)
        {
            return "The section opens and closes at different depths of one paragraph — one end inside a hyperlink or a field and the other not. Put both ends at the same place.";
        }

        if (po.Parent == pc.Parent)
        {
            bool before = po.Descendants().TakeWhile(e => e != open).Any(IsContent);
            bool after = pc.Descendants().SkipWhile(e => e != close).Skip(1).Where(e => !e.Ancestors().Contains(close)).Any(IsContent);

            if (before || after)
            {
                return summary
                    ? "A summary section's $<$summary> and $</> stand in paragraphs of their own."
                    : Splice(id, open, close, po, pc);
            }

            po.AddBeforeSelf(opening);
            open.Remove();
            pc.AddAfterSelf(closing);
            close.Remove();

            foreach (XElement p in new[] { po, pc }.Distinct())
            {
                if (p.Parent is not null && !HasContent(p))
                {
                    p.Remove();
                }
            }

            return null;
        }

        XElement? ro = po.Ancestors(W + "tr").FirstOrDefault();
        XElement? rc = pc.Ancestors(W + "tr").FirstOrDefault();

        if (ro is not null && rc is not null && ro.Parent == rc.Parent && !ro.IsAfter(rc))
        {
            ro.AddBeforeSelf(opening);
            rc.AddAfterSelf(closing);
            open.Remove();
            close.Remove();

            foreach (XElement p in new[] { po, pc }.Distinct())
            {
                if (p.Parent is { } cell && cell.Name == W + "tc" && cell.Elements(W + "p").Count() > 1 && !HasContent(p))
                {
                    p.Remove();
                }
            }

            return null;
        }

        return "A section's ${#…} or ${if …} and its ${/} are in one paragraph, in paragraphs side by side, or in rows of one table; these are not. Move the ${/} beside its section.";
    }

    /// <summary>
    /// A section over paragraphs whose ends share their paragraphs with text — <c>Before ${if x}</c> … <c>${/} after</c>,
    /// as public templates write them — is spliced as the XML between its ends: its first paragraph's runs after the
    /// opening join the paragraph before them, its last paragraph's runs before the closing begin the paragraph after.
    /// Written once, the document is the template without its tags; not at all, the text before the opening and after the
    /// closing make one paragraph. ADR-301 §5.12 (INFERRED: the reading that keeps Word's XML whole, which these templates
    /// need; Survey123's pages say nothing of it).
    /// </summary>
    /// <returns>The refusal, or null.</returns>
    private static string? Splice(int id, XElement open, XElement close, XElement po, XElement pc)
    {
        if (open.Parent != po || close.Parent != pc)
        {
            return "The section's ends are inside a hyperlink or a field of their paragraphs: put them in the paragraphs' own text.";
        }

        static XElement Shell(XElement paragraph, bool join) =>
            new(W + "p", paragraph.Attributes(), join ? new XAttribute(Join, "1") : null,
                paragraph.Element(W + "pPr") is { } properties ? new XElement(properties) : null);

        static XElement Holding(XElement shell, IEnumerable<XNode> nodes)
        {
            foreach (XNode node in nodes.ToList())
            {
                node.Remove();
                shell.Add(node);
            }

            return shell;
        }

        // Before ${#…} | ${#…} | the rest of its paragraph … the start of the last paragraph | ${/} | after ${/}: each end's
        // paragraph is cut in two at it, and the half before an end joins the paragraph written after it (Join).
        XElement head = Holding(Shell(po, join: true), open.NodesBeforeSelf().Where(n => n is not XElement { Name.LocalName: "pPr" }));
        XElement first = Holding(Shell(po, join: false), open.NodesAfterSelf());
        XElement last = Holding(Shell(pc, join: true), close.NodesBeforeSelf().Where(n => n is not XElement { Name.LocalName: "pPr" }));
        XElement tail = Holding(Shell(pc, join: false), close.NodesAfterSelf());

        po.ReplaceWith(head, new XElement(G + "open", new XAttribute("id", id)), first);
        pc.ReplaceWith(last, new XElement(G + "close", new XAttribute("id", id)), tail);
        return null;
    }

    /// <summary>
    /// Joins each paragraph a splice marked to the paragraph written after it: its properties and runs, then the next
    /// one's runs. A marked paragraph with none after it is left as it is.
    /// </summary>
    private static void JoinParagraphs(XElement root)
    {
        foreach (XElement container in root.DescendantsAndSelf().Where(e => e.Elements(W + "p").Any(p => p.Attribute(Join) is not null)).ToList())
        {
            XElement? carried = null;

            foreach (XElement child in container.Elements().ToList())
            {
                if (child.Name != W + "p")
                {
                    if (carried is not null)
                    {
                        child.AddBeforeSelf(carried);
                        carried = null;
                    }

                    continue;
                }

                bool joins = child.Attribute(Join) is not null;

                if (carried is null)
                {
                    if (joins)
                    {
                        child.Attribute(Join)!.Remove();
                        carried = new XElement(child);
                        child.Remove();
                    }

                    continue;
                }

                carried.Add(child.Nodes().Where(n => n is not XElement { Name.LocalName: "pPr" }).ToList());

                if (joins)
                {
                    child.Remove();
                    continue;
                }

                child.ReplaceWith(carried);
                carried = null;
            }

            if (carried is not null)
            {
                container.Add(carried);
            }
        }
    }

    /// <summary>Compiles sibling nodes into static content, elements to fill, values and sections.</summary>
    private static List<Node> Compile(IReadOnlyList<XNode> nodes, Dictionary<int, ReportTag?> tags, Dictionary<int, ReportIssue> places)
    {
        List<Node> compiled = [];

        for (int i = 0; i < nodes.Count; i++)
        {
            XNode node = nodes[i];

            if (node is not XElement element)
            {
                compiled.Add(new StaticNode(node));
                continue;
            }

            if (element.Name == G + "open")
            {
                int id = (int)element.Attribute("id")!;
                int end = i + 1;

                while (end < nodes.Count && !(nodes[end] is XElement c && c.Name == G + "close" && (int)c.Attribute("id")! == id))
                {
                    end++;
                }

                compiled.Add(new SectionNode(tags[id], Compile([.. nodes.Skip(i + 1).Take(end - i - 1)], tags, places), places[id]));
                i = end;
                continue;
            }

            if (element.Name == G + "close")
            {
                continue;
            }

            if (element.Name == G + "tag")
            {
                int id = (int)element.Attribute("id")!;
                compiled.Add(new ValueNode(tags[id], element.Element(W + "rPr"), places[id]));
                continue;
            }

            if (element.Descendants().Any(d => d.Name.Namespace == G))
            {
                XElement shell = new(element.Name, element.Attributes());
                compiled.Add(new ElementNode(shell, Compile([.. element.Nodes()], tags, places)));
                continue;
            }

            compiled.Add(new StaticNode(element));
        }

        return compiled;
    }

    // ------------------------------------------------------------------------------------------------------ checking

    /// <summary>Every problem the template has against this form: its syntax, and each name, method and section.</summary>
    public IReadOnlyList<ReportIssue> Check(ReportSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        List<ReportIssue> issues = [.. _syntax];
        ReportEvaluator evaluator = new(schema, DateTimeOffset.UtcNow, TimeSpan.Zero);

        foreach (Part part in _parts.Values)
        {
            CheckList(part.Tree, new ReportScope(null, schema.Root, schema.TopName, null, 1, null, false), evaluator, issues);
        }

        return issues;
    }

    private void CheckList(List<Node> nodes, ReportScope scope, ReportEvaluator evaluator, List<ReportIssue> issues)
    {
        foreach (Node node in nodes)
        {
            switch (node)
            {
                case ElementNode e:
                    CheckList(e.Children, scope, evaluator, issues);
                    break;
                case ValueNode { Tag: { } tag } v:
                    try
                    {
                        evaluator.Check(tag, scope);
                        ReadsAllFiles |= Names(tag.Expression).Any(n => string.Equals(n, "$attachment", StringComparison.OrdinalIgnoreCase));
                    }
                    catch (ReportSyntaxException ex)
                    {
                        issues.Add(v.Place with { Message = ex.Message });
                    }

                    break;
                case SectionNode { Tag: { } tag } s:
                    ReportScope inner = scope;

                    try
                    {
                        inner = evaluator.CheckSection(tag, scope);
                        ReadsAllFiles |= tag.Expression is ReportName { Path: [var only] } && string.Equals(only, "$attachment", StringComparison.OrdinalIgnoreCase);
                    }
                    catch (ReportSyntaxException ex)
                    {
                        issues.Add(s.Place with { Message = ex.Message });
                    }

                    CheckList(s.Body, inner, evaluator, issues);
                    break;
                case SectionNode s:
                    CheckList(s.Body, scope, evaluator, issues);
                    break;
                default:
                    break;
            }
        }
    }

    private static IEnumerable<string> Names(ReportNode? node) => node switch
    {
        ReportName n => [n.Path[0]],
        ReportUnary u => Names(u.Operand),
        ReportPiped p => Names(p.Inner),
        ReportBinary b => Names(b.Left).Concat(Names(b.Right)),
        _ => [],
    };

    /// <summary>The questions whose files the template reads, so only their attachments' bytes are fetched.</summary>
    public IReadOnlySet<string> FileQuestions(ReportSchema schema)
    {
        HashSet<string> found = new(StringComparer.OrdinalIgnoreCase);

        foreach (FormQuestion q in schema.Definition.AllQuestions.Where(ReportSchema.IsFiles))
        {
            foreach (Part part in _parts.Values)
            {
                if (part.Tags.Values.Any(t => t is not null && Names(t.Expression).Any(n => string.Equals(n, q.Name, StringComparison.OrdinalIgnoreCase)
                    || (t.Expression is ReportName dotted && dotted.Path.Count == 2 && string.Equals(dotted.Path[1], q.Name, StringComparison.OrdinalIgnoreCase)))))
                {
                    found.Add(q.Name);
                }
            }
        }

        return found;
    }

    // ------------------------------------------------------------------------------------------------------- filling

    /// <summary>The state of one document being written: its pictures and the ids Word needs unique.</summary>
    private sealed class Writing
    {
        public Dictionary<string, List<(string Id, string Target)>> Relationships { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, byte[]> Media { get; } = new(StringComparer.Ordinal);

        public Dictionary<(ReportFile File, string Part), string> Placed { get; } = [];

        public Dictionary<ReportFile, string> MediaOf { get; } = [];

        public HashSet<string> Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Drawing { get; set; } = 50_000;

        public long Bytes { get; set; }
    }

    /// <summary>The most picture bytes one document carries (INFERRED), so a report of many photographs stays bounded.</summary>
    public const long MaximumPictureBytes = 200L * 1024 * 1024;

    /// <summary>
    /// Fills the template with one record, or with several merged into one document — each record's body after the last,
    /// a page break between them, headers and footers filled with the first (ADR-301 §5.6, INFERRED).
    /// </summary>
    /// <exception cref="ReportTemplateException">The pictures exceed <see cref="MaximumPictureBytes"/>.</exception>
    public byte[] Fill(IReadOnlyList<ReportRecord> records, ReportEvaluator evaluator, IReportLookups? lookups = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(evaluator);

        if (records.Count == 0)
        {
            throw new ArgumentException("A report is made of at least one record.", nameof(records));
        }

        // What the template reads beside the records — maps, queries — comes from the lookups; with none, nothing is fetched
        // and those placeholders write nothing.
        evaluator.Lookups = lookups ?? new ReportPrefetch();
        Writing writing = new();
        ReportScope summary = new(null, evaluator.Schema.Root, evaluator.Schema.TopName, null, 1, null, false) { Inputs = records };
        Dictionary<string, byte[]> written = new(StringComparer.Ordinal);
        ReportScope Top(ReportRecord record, int position) =>
            new(null, evaluator.Schema.Root, evaluator.Schema.TopName, record, position, null, false);

        foreach (Part part in _parts.Values)
        {
            XElement root = part.Prepared.Root!;
            XElement output = new(root.Name, root.Attributes());

            if (part.Name == DocumentPart && root.Element(W + "body") is { } bodyTemplate)
            {
                // The body is filled once per record; its last child, the section properties, once at the end.
                List<Node> content = part.Tree.OfType<ElementNode>().FirstOrDefault(n => n.Shell.Name == W + "body") is { } bodyNode
                    ? [.. bodyNode.Children]
                    : [.. bodyTemplate.Nodes().Select(n => (Node)new StaticNode(n))];
                XElement body = new(bodyTemplate.Name, bodyTemplate.Attributes());
                Node? sections = content.LastOrDefault(n => n is StaticNode { Content: XElement e } && e.Name == W + "sectPr");

                if (sections is not null)
                {
                    content.Remove(sections);
                }

                // Summary sections at the start and the end are written once, for all the records (Survey123's page);
                // what is between them is written once per record. A template of summaries alone writes no record.
                int lead = 0;

                for (int k = 0; k < content.Count && (IsSummary(content[k]) || IsBlank(content[k])); k++)
                {
                    lead = IsSummary(content[k]) ? k + 1 : lead;
                }

                int trail = content.Count;

                for (int k = content.Count - 1; k >= lead && (IsSummary(content[k]) || IsBlank(content[k])); k--)
                {
                    trail = IsSummary(content[k]) ? k : trail;
                }

                List<Node> perRecord = content.GetRange(lead, trail - lead);
                bool writesRecords = !HasSummary || perRecord.Any(n => !IsBlank(n));

                foreach (SectionNode leading in content.Take(lead).OfType<SectionNode>())
                {
                    body.Add(Render(leading.Body, summary, evaluator, writing, part.Name));
                }

                for (int i = 0; writesRecords && i < records.Count; i++)
                {
                    if (i > 0)
                    {
                        body.Add(new XElement(W + "p", new XElement(W + "r", new XElement(W + "br", new XAttribute(W + "type", "page")))));
                    }

                    body.Add(Render(perRecord, Top(records[i], i + 1), evaluator, writing, part.Name));
                }

                foreach (SectionNode trailing in content.Skip(trail).OfType<SectionNode>())
                {
                    body.Add(Render(trailing.Body, summary, evaluator, writing, part.Name));
                }

                if (!body.Elements().Any())
                {
                    body.Add(new XElement(W + "p"));
                }

                if (sections is StaticNode s)
                {
                    body.Add(Clone(s.Content));
                }

                foreach (Node other in part.Tree)
                {
                    bool isBody = other is ElementNode { Shell: var shell } && shell.Name == W + "body"
                        || other is StaticNode { Content: XElement { } plain } && plain.Name == W + "body";
                    output.Add(isBody ? body : Render([other], Top(records[0], 1), evaluator, writing, part.Name));
                }
            }
            else
            {
                output.Add(Render(part.Tree, Top(records[0], 1), evaluator, writing, part.Name));
            }

            JoinParagraphs(output);
            XDocument document = new(part.Prepared.Declaration, output);
            written[part.Name] = Save(document);
        }

        return Package(written, writing);
    }

    /// <summary>Writes the package: the template's own parts, the filled ones, the pictures and their relationships.</summary>
    private byte[] Package(Dictionary<string, byte[]> written, Writing writing)
    {
        Dictionary<string, byte[]> parts = new(_entries, StringComparer.Ordinal);

        foreach ((string name, byte[] bytes) in written)
        {
            parts[name] = bytes;
        }

        foreach ((string part, List<(string Id, string Target)> relationships) in writing.Relationships)
        {
            string folder = Path.GetDirectoryName(part)!.Replace('\\', '/');
            string relsName = $"{folder}/_rels/{Path.GetFileName(part)}.rels";
            XDocument rels = parts.TryGetValue(relsName, out byte[]? existing)
                ? Load(existing)
                : new XDocument(new XElement(PackageRels + "Relationships"));

            foreach ((string id, string target) in relationships)
            {
                rels.Root!.Add(new XElement(PackageRels + "Relationship",
                    new XAttribute("Id", id), new XAttribute("Type", ImageRelationship), new XAttribute("Target", target)));
            }

            parts[relsName] = Save(rels);
        }

        foreach ((string name, byte[] bytes) in writing.Media)
        {
            parts[name] = bytes;
        }

        if (writing.Extensions.Count > 0)
        {
            XDocument types = Load(parts["[Content_Types].xml"]);

            foreach (string extension in writing.Extensions)
            {
                if (!types.Root!.Elements(ContentTypes + "Default").Any(d => string.Equals((string?)d.Attribute("Extension"), extension, StringComparison.OrdinalIgnoreCase)))
                {
                    types.Root.AddFirst(new XElement(ContentTypes + "Default",
                        new XAttribute("Extension", extension), new XAttribute("ContentType", extension == "jpeg" ? "image/jpeg" : $"image/{extension}")));
                }
            }

            parts["[Content_Types].xml"] = Save(types);
        }

        using MemoryStream output = new();

        using (ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            // [Content_Types].xml first, as Word writes it; then the template's order; then what was added.
            foreach (string name in parts.Keys.OrderBy(n => n == "[Content_Types].xml" ? 0 : 1)
                .ThenBy(n => _order.IndexOf(n) is var i && i < 0 ? int.MaxValue : i).ThenBy(n => n, StringComparer.Ordinal))
            {
                ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using Stream stream = entry.Open();
                stream.Write(parts[name]);
            }
        }

        return output.ToArray();
    }

    private static IEnumerable<XNode> Render(List<Node> nodes, ReportScope scope, ReportEvaluator evaluator, Writing writing, string part)
    {
        foreach (Node node in nodes)
        {
            switch (node)
            {
                case StaticNode s:
                    yield return Clone(s.Content);
                    break;
                case ElementNode e:
                    XElement made = new(e.Shell.Name, e.Shell.Attributes(), Render(e.Children, scope, evaluator, writing, part));

                    // A cell holds at least one paragraph, and a table at least one row, or Word calls the file damaged.
                    if (made.Name == W + "tc" && made.Elements().LastOrDefault()?.Name != W + "p")
                    {
                        made.Add(new XElement(W + "p"));
                    }

                    if (made.Name == W + "tbl" && !made.Elements(W + "tr").Any())
                    {
                        break;
                    }

                    yield return made;
                    break;
                case ValueNode { Tag: { } tag } v:
                    foreach (XNode run in Write(evaluator.Write(tag, scope), v.RunProperties, writing, part))
                    {
                        yield return run;
                    }

                    break;
                case SectionNode { Tag: { } tag } section:
                    foreach (ReportScope inner in evaluator.Sections(tag, scope))
                    {
                        foreach (XNode child in Render(section.Body, inner, evaluator, writing, part))
                        {
                            yield return child;
                        }
                    }

                    break;
                default:
                    break;
            }
        }
    }

    private static XElement RunText(XElement? properties, string text) =>
        new(W + "r", properties is null ? null : new XElement(properties), new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text));

    /// <summary>The runs a value writes, formatted as its placeholder was.</summary>
    private static IEnumerable<XNode> Write(ReportOutput output, XElement? properties, Writing writing, string part)
    {
        switch (output)
        {
            case ReportText { Multiline: true } text:
                string[] lines = text.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                XElement run = new(W + "r", properties is null ? null : new XElement(properties));

                for (int i = 0; i < lines.Length; i++)
                {
                    if (i > 0)
                    {
                        run.Add(new XElement(W + "br"));
                    }

                    run.Add(new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), lines[i]));
                }

                yield return run;
                break;
            case ReportText text:
                if (text.Text.Length > 0)
                {
                    yield return RunText(properties, Clean(text.Text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ')));
                }

                break;
            case ReportBullets bullets:
                XElement list = new(W + "r", properties is null ? null : new XElement(properties));

                for (int i = 0; i < bullets.Items.Count; i++)
                {
                    if (i > 0)
                    {
                        list.Add(new XElement(W + "br"));
                    }

                    list.Add(new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), "• " + Clean(bullets.Items[i])));
                }

                if (bullets.Items.Count > 0)
                {
                    yield return list;
                }

                break;
            case ReportImage image:
                yield return Picture(image, properties, writing, part);
                break;
            default:
                break;
        }
    }

    /// <summary>Text as XML may hold it: control characters an answer can carry are dropped rather than failing the save.</summary>
    private static string Clean(string text) =>
        text.Any(c => c < 0x20 && c is not '\t' and not '\n' and not '\r') ? new string([.. text.Where(c => c >= 0x20 || c is '\t')]) : text;

    private static XElement Picture(ReportImage image, XElement? properties, Writing writing, string part)
    {
        (string extension, _, int pw, int ph) = ReportPicture.Kind(image.File.Content)!.Value;

        if (!writing.MediaOf.TryGetValue(image.File, out string? media))
        {
            writing.Bytes += image.File.Content!.Length;

            if (writing.Bytes > MaximumPictureBytes)
            {
                throw new ReportTemplateException($"The report's pictures come to more than {MaximumPictureBytes / (1024 * 1024)} MB; choose fewer records.");
            }

            media = $"media/graticula-report-{writing.MediaOf.Count + 1}.{extension}";
            writing.MediaOf[image.File] = media;
            writing.Media["word/" + media] = image.File.Content!;
            writing.Extensions.Add(extension);
        }

        if (!writing.Placed.TryGetValue((image.File, part), out string? relationship))
        {
            if (!writing.Relationships.TryGetValue(part, out List<(string Id, string Target)>? list))
            {
                list = [];
                writing.Relationships[part] = list;
            }

            relationship = $"rIdGraticula{list.Count + 1}";
            list.Add((relationship, media));
            writing.Placed[(image.File, part)] = relationship;
        }

        (double w, double h) = ReportPicture.Placed(pw, ph, image.Width, image.Height, image.MaxWidth, image.MaxHeight);
        long cx = (long)Math.Round(w * ReportPicture.EmuPerPixel);
        long cy = (long)Math.Round(h * ReportPicture.EmuPerPixel);
        int id = ++writing.Drawing;
        string name = image.File.Name;

        return new XElement(W + "r", properties is null ? null : new XElement(properties),
            new XElement(W + "drawing", new XAttribute(XNamespace.Xmlns + "wp", Wp.NamespaceName),
                new XElement(Wp + "inline", new XAttribute(XNamespace.Xmlns + "a", A.NamespaceName), new XAttribute(XNamespace.Xmlns + "pic", Pic.NamespaceName),
                    new XAttribute("distT", 0), new XAttribute("distB", 0), new XAttribute("distL", 0), new XAttribute("distR", 0),
                    new XElement(Wp + "extent", new XAttribute("cx", cx), new XAttribute("cy", cy)),
                    new XElement(Wp + "docPr", new XAttribute("id", id), new XAttribute("name", $"Picture {id}"), new XAttribute("descr", name)),
                    new XElement(Wp + "cNvGraphicFramePr", new XElement(A + "graphicFrameLocks", new XAttribute("noChangeAspect", 1))),
                    new XElement(A + "graphic",
                        new XElement(A + "graphicData", new XAttribute("uri", Pic.NamespaceName),
                            new XElement(Pic + "pic",
                                new XElement(Pic + "nvPicPr",
                                    new XElement(Pic + "cNvPr", new XAttribute("id", 0), new XAttribute("name", name)),
                                    new XElement(Pic + "cNvPicPr")),
                                new XElement(Pic + "blipFill",
                                    new XElement(A + "blip", new XAttribute(R + "embed", relationship)),
                                    new XElement(A + "stretch", new XElement(A + "fillRect"))),
                                new XElement(Pic + "spPr",
                                    new XElement(A + "xfrm", image.Rotate is > 0 and < 360 ? new XAttribute("rot", (long)Math.Round(image.Rotate * 60000)) : null,
                                        new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)),
                                        new XElement(A + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy))),
                                    new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst")))))))));
    }

    // ------------------------------------------------------------------------------------------------- the name

    /// <summary>
    /// A report's file name from the name a person writes: text and value placeholders (<c>${asset_id}</c>,
    /// <c>${inspected | format:"YYYYMMDD"}</c>), the way Survey123's <i>Report name</i> box reads it, cleaned for a file system.
    /// </summary>
    /// <exception cref="ReportSyntaxException">A placeholder in it cannot be read, or is a section.</exception>
    public static string Name(string pattern, ReportRecord? record, ReportEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(evaluator);
        StringBuilder name = new();
        int at = 0;
        ReportScope scope = new(null, evaluator.Schema.Root, evaluator.Schema.TopName, record, 1, null, false);

        foreach ((int start, int end) in Placeholders(pattern))
        {
            name.Append(pattern, at, start - at);
            ReportTag tag = ReportExpression.Parse(pattern[(start + 2)..(end - 1)]);

            if (tag.Kind != ReportTagKind.Value)
            {
                throw new ReportSyntaxException("A report's name takes values, as ${asset_id}; a section or an if is not read there.");
            }

            evaluator.Check(tag, scope);

            if (record is not null)
            {
                name.Append(evaluator.Write(tag, scope) switch
                {
                    ReportText t => t.Text,
                    ReportBullets b => string.Join(", ", b.Items),
                    ReportImage i => i.File.Name,
                    _ => string.Empty,
                });
            }

            at = end;
        }

        name.Append(pattern, at, pattern.Length - at);
        return FileName(name.ToString());
    }

    /// <summary>A name a file system and a ZIP hold: no path, no control or reserved characters, at most 120 characters.</summary>
    public static string FileName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        char[] invalid = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];
        string cleaned = new([.. name.Select(c => c < 0x20 || invalid.Contains(c) ? '_' : c)]);
        cleaned = cleaned.Trim().Trim('.').Trim();

        if (cleaned.Length > 120)
        {
            cleaned = cleaned[..120].TrimEnd();
        }

        return cleaned.Length == 0 ? "report" : cleaned;
    }
}
