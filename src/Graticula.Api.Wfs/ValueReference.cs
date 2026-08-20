using System;

namespace Graticula.Api.Wfs;

/// <summary>
/// Reads a <c>fes:ValueReference</c>, which is an XPath expression.
/// </summary>
/// <remarks>
/// <para>
/// <b>The subset ISO 19143 calls minimum XPath, and no more.</b> A conforming
/// Basic WFS must accept the abbreviated forms that name one property of the
/// feature: the bare name, the same name with a namespace prefix, and either of
/// those behind <c>./</c>. That is what a client generates when it means *this
/// property of this feature*, and it is the whole of what this surface can answer.
/// </para>
/// <para>
/// <b>Everything else is refused rather than approximated.</b> A path with a
/// predicate, an axis, a wildcard or more than one step is asking a question about
/// document structure, and this server's features have one level of structure.
/// Guessing which property <c>//*[3]</c> meant is how a filter silently reads a
/// different column than the caller wrote.
/// </para>
/// </remarks>
public static class ValueReference
{
    /// <summary>What a reference points at.</summary>
    public enum Kind
    {
        /// <summary>A property of the feature.</summary>
        Property,

        /// <summary>An attribute, of which this server has one: <c>gml:id</c>.</summary>
        Attribute,
    }

    /// <summary>
    /// Resolves a reference, saying whether it names a property or an attribute.
    /// </summary>
    /// <remarks>
    /// <b><c>@gml:id</c> is the attribute that matters and the conformance suite
    /// asks for it by name.</b> Minimum XPath includes the abbreviated attribute
    /// form, and on a simple feature there is exactly one attribute worth asking
    /// for: the identifier. Every other attribute reference is refused, because
    /// this server's features carry no others.
    /// </remarks>
    /// <param name="path">The expression, as the client wrote it.</param>
    /// <param name="kind">Whether it names a property or an attribute.</param>
    /// <param name="localName">The name it resolves to.</param>
    /// <param name="fault">Why it was refused.</param>
    /// <returns>Whether it resolved.</returns>
    public static bool TryResolve(
        string? path, out Kind kind, out string localName, out WfsFault? fault)
    {
        kind = Kind.Property;
        localName = string.Empty;
        fault = null;

        string text = (path ?? string.Empty).Trim();

        if (text.StartsWith("./", StringComparison.Ordinal))
        {
            text = text[2..].Trim();
        }

        if (text.StartsWith('@') || text.StartsWith("attribute::", StringComparison.Ordinal))
        {
            string attribute = text.StartsWith('@') ? text[1..] : text["attribute::".Length..];

            int colon = attribute.LastIndexOf(':');
            string local = colon >= 0 ? attribute[(colon + 1)..] : attribute;

            if (!string.Equals(local, "id", StringComparison.Ordinal))
            {
                fault = new WfsFault(
                    WfsFaultCode.OperationNotSupported,
                    "valueReference",
                    $"'{path}' names an attribute this server's features do not carry. The only "
                    + "attribute a feature has is gml:id.");

                return false;
            }

            kind = Kind.Attribute;
            localName = "id";
            return true;
        }

        return TryLocalName(text, out localName, out fault);
    }

    /// <summary>The property a reference names.</summary>
    /// <param name="path">The expression, as the client wrote it.</param>
    /// <param name="localName">The property name it resolves to.</param>
    /// <param name="fault">Why it was refused.</param>
    /// <returns>Whether it resolved.</returns>
    public static bool TryLocalName(string? path, out string localName, out WfsFault? fault)
    {
        localName = string.Empty;
        fault = null;

        string text = (path ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            fault = WfsFault.Invalid("valueReference", "A ValueReference must name a property.");
            return false;
        }

        // The two abbreviations that mean "of this feature".
        if (text.StartsWith("./", StringComparison.Ordinal))
        {
            text = text[2..].Trim();
        }
        else if (text.StartsWith("child::", StringComparison.Ordinal))
        {
            text = text[7..].Trim();
        }

        if (text.IndexOfAny(['/', '[', ']', '(', ')', '*', '@', ':', ' ']) >= 0)
        {
            // A prefix is legal, so a single colon is unpicked before the refusal.
            int colon = text.LastIndexOf(':');

            string candidate = colon >= 0 ? text[(colon + 1)..] : text;

            if (colon < 0
                || candidate.Length == 0
                || candidate.IndexOfAny(['/', '[', ']', '(', ')', '*', '@', ':', ' ']) >= 0
                || text[..colon].IndexOfAny(['/', '[', ']', '(', ')', '*', '@', ' ']) >= 0)
            {
                fault = new WfsFault(
                    WfsFaultCode.OperationNotSupported,
                    "valueReference",
                    $"'{path}' is an XPath expression this server does not evaluate. It reads a "
                    + "reference that names one property, optionally with a namespace prefix and "
                    + "optionally behind './'.");

                return false;
            }

            text = candidate;
        }

        localName = text;
        return true;
    }
}
