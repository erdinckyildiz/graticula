using System.IO;
using System.Text;
using System.Xml;

namespace Graticula.Api.Wfs;

/// <summary>
/// How this surface reads and writes XML.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hardened before the first document, not after the first finding.</b> Until
/// ADR-039 there was no XML anywhere in this server, so the whole class of XML
/// attack arrives with this project. The Security gate has already failed once on
/// caller text reaching a parser nobody had checked
/// ([D-41](../../../docs/architecture-debt.md)), and the lesson recorded there was
/// about the method rather than the finding: what was audited was the code's own
/// account of itself. So the settings live in one place, they are the only way
/// this project constructs a reader, and a test asserts each one by removing it.
/// </para>
/// <para>
/// <b>What each closes.</b> <c>DtdProcessing.Prohibit</c> stops the billion-laughs
/// entity expansion and every doctype-driven trick outright — refusing the DTD is
/// stronger than bounding it, and no WFS request needs one. A null
/// <see cref="XmlResolver"/> stops external entity resolution, which is the one
/// that reads files off this server's disk and makes requests from inside its
/// network. <c>MaxCharactersInDocument</c> bounds the whole request, because a
/// filter is a language and a language is arbitrarily long.
/// <c>MaxCharactersFromEntities</c> bounds entity expansion, and it is a second
/// line rather than the first: with the DTD prohibited there are no entities to
/// expand, and it starts mattering the day somebody relaxes that. <b>It is
/// deliberately not zero</b> — zero means <em>no limit</em> in
/// <see cref="XmlReaderSettings"/>, which is the opposite of what it reads like,
/// and it sat here saying the wrong thing until a falsification run went looking
/// for which of these settings actually did anything. Nesting depth is not a
/// setting at all and is counted by the readers that walk the tree.
/// </para>
/// <para>
/// <b>Readers, plural, and that sentence said reader for a day.</b> There are two
/// — <c>FilterReader</c> over the predicates and <c>GmlGeometryReader</c> over the
/// geometry inside them — and only the first counted. A geometry collection may
/// hold collections, so a <c>gml:MultiSurface</c> three thousand levels deep in a
/// 223 KB unauthenticated POST recursed past the stack and killed the process,
/// which .NET cannot catch and nothing can log. **The bounds on this page held
/// perfectly throughout**; they were an order of magnitude too generous to reach.
/// Found by an independent reviewer, and the wording here is the finding as much
/// as the code was: a remark that named one guard while claiming coverage for two
/// is the shape [D-41](../../../docs/architecture-debt.md) already recorded once.
/// The budget is now shared across both readers rather than counted twice.
/// </para>
/// </remarks>
public static class SafeXml
{
    /// <summary>The largest request document this surface will read.</summary>
    /// <remarks>
    /// A megabyte of filter is far past anything a client generates from a map
    /// extent and a few predicates, and small enough that parsing cannot be made
    /// expensive on purpose. The same reasoning as
    /// <c>WhereClause.MaximumLength</c>, one order of magnitude up, because XML
    /// spends most of its bytes on tags.
    /// </remarks>
    public const long MaximumDocumentCharacters = 1_048_576;

    /// <summary>The largest request body this surface will buffer.</summary>
    /// <remarks>
    /// <para>
    /// <b>A byte bound beside the character bound, because the reader cannot
    /// enforce one on a stream it is not allowed to read.</b> A POST body arrives
    /// on a server stream that refuses synchronous reads, so it is copied into
    /// memory first and parsed from there — and a copy with no ceiling is a way to
    /// exhaust this process from an unauthenticated request.
    /// </para>
    /// <para>
    /// Four times <see cref="MaximumDocumentCharacters"/>, which is what UTF-8
    /// costs in the worst case. Anything that survives this bound still meets the
    /// character bound inside the parser, so the two are a floor and a ceiling on
    /// the same limit rather than two different policies.
    /// </para>
    /// </remarks>
    public const int MaximumRequestBytes = 4 * (int)MaximumDocumentCharacters;

    /// <summary>How deep a request document may nest.</summary>
    /// <remarks>
    /// Recursive descent over an element tree recurses once per level, and .NET
    /// cannot catch a stack overflow. A hand-written filter is three or four deep;
    /// a generated one with a multipolygon is perhaps eight.
    /// </remarks>
    public const int MaximumDepth = 32;

    /// <summary>Settings for reading a request document.</summary>
    public static XmlReaderSettings ReaderSettings => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaximumDocumentCharacters,
        MaxCharactersFromEntities = MaximumDocumentCharacters,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CloseInput = false,
        Async = true,
    };

    /// <summary>Settings for writing a response document.</summary>
    /// <remarks>
    /// <b>No indentation.</b> A feature collection is machine-read and may hold
    /// fifty thousand features; whitespace on every element is bytes on the wire
    /// paid on every request to help a reader who is almost never a person. The
    /// documents small enough for a person to read — capabilities, a schema, an
    /// exception report — are indented by whatever they are opened in.
    /// </remarks>
    public static XmlWriterSettings WriterSettings => new()
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = false,
        Async = true,
        CloseOutput = false,
        NamespaceHandling = NamespaceHandling.OmitDuplicates,
    };

    /// <summary>Opens a hardened reader over a request body.</summary>
    /// <param name="stream">The body.</param>
    /// <returns>The reader.</returns>
    public static XmlReader Read(Stream stream) => XmlReader.Create(stream, ReaderSettings);

    /// <summary>Opens a hardened reader over request text.</summary>
    /// <param name="text">The document.</param>
    /// <returns>The reader.</returns>
    public static XmlReader Read(string text) =>
        XmlReader.Create(new StringReader(text), ReaderSettings);
}
