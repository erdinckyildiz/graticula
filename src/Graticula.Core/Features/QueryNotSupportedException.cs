using System;

namespace Graticula.Features;

/// <summary>
/// A well-formed query the layer's provider does not answer — refused, never approximated.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-008 §2's rule, given a type.</b> A provider that cannot evaluate a relation must say so
/// rather than quietly answer a different question, and until a second provider existed every
/// refusal of that kind was a sentence the endpoint wrote before it reached the source. A layer
/// served from a GeoParquet file (ADR-066) answers <c>intersects</c> and the envelope relations
/// and nothing else, and that difference is known only to the source — so the source is where the
/// refusal is raised, and this is how it travels to a 400 rather than a 500.
/// </para>
/// <para>
/// <b>The message is written for the caller</b>, because it is returned to them: it names what
/// was asked and what the layer does answer.
/// </para>
/// </remarks>
public sealed class QueryNotSupportedException : Exception
{
    /// <summary>Creates the refusal with no message.</summary>
    public QueryNotSupportedException()
    {
    }

    /// <summary>Creates the refusal.</summary>
    /// <param name="message">What was asked, and what this layer answers instead.</param>
    public QueryNotSupportedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the refusal with its cause.</summary>
    /// <param name="message">What was asked, and what this layer answers instead.</param>
    /// <param name="innerException">The cause.</param>
    public QueryNotSupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
