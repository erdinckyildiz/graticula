using System;
using System.Text.Json;

namespace Graticula.Api.OgcFeatures;

/// <summary>
/// A refusal, as RFC 7807 writes one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The status is truthful here, unlike on the older surfaces.</b> WFS and WMS
/// both answer their refusals with HTTP 200 and an exception document inside,
/// because their clients were written that way and several never read the body of a
/// 4xx. OGC API Features is HTTP-native: §7.3 says the status code carries the
/// outcome and the body explains it. **So this is the first face on this server
/// where a refusal is a 400 and a proxy, a log and a monitor can all see it.**
/// </para>
/// <para>
/// <b><c>title</c> is for the class of problem and <c>detail</c> for this one.</b>
/// A client groups by the first and shows the second, so putting the whole message
/// in the title gives every error its own category.
/// </para>
/// </remarks>
/// <param name="Status">The HTTP status this is served with.</param>
/// <param name="Title">The class of problem, short and stable.</param>
/// <param name="Detail">What went wrong this time.</param>
public sealed record OgcProblem(int Status, string Title, string Detail)
{
    /// <summary>The parameters or the path are wrong.</summary>
    /// <param name="detail">What is wrong.</param>
    /// <returns>The problem.</returns>
    public static OgcProblem BadRequest(string detail) => new(400, "Bad Request", detail);

    /// <summary>No such resource, or none this caller may see.</summary>
    /// <remarks>
    /// <b>A collection somebody may not see is absent, not forbidden.</b> Answering
    /// 403 would tell an anonymous caller that a private layer exists and what it is
    /// called, which is the sharing model leaking through the error code — the same
    /// rule the ArcGIS and WFS faces already follow.
    /// </remarks>
    /// <param name="detail">What was not found.</param>
    /// <returns>The problem.</returns>
    public static OgcProblem NotFound(string detail) => new(404, "Not Found", detail);

    /// <summary>A request this deployment is configured not to answer.</summary>
    /// <param name="detail">Which setting refused it, in the operator's words.</param>
    /// <remarks>
    /// <b>403 rather than 404, and the reason is [D-179](../../docs/architecture-debt.md).</b>
    /// A capability the service has turned off is not an absent collection: the collection
    /// is listed, readable and linked, and hiding it at the moment somebody writes to it
    /// would be a different answer from the one the same server gives on its ArcGIS face.
    /// ADR-018 makes a turned-off <em>face</em> a 404 so nothing leaks; this is a
    /// capability inside a live face, which leaks nothing a client has not already read.
    /// </remarks>
    public static OgcProblem Forbidden(string detail) => new(403, "Forbidden", detail);

    /// <summary>The catalogue cannot be read and nothing is remembered.</summary>
    /// <remarks>
    /// <b>503 rather than 404, and the difference is the whole of
    /// [D-127](../../docs/architecture-debt.md).</b> A server that cannot list its collections
    /// has not learnt that they are gone; answering 404 — or an empty `/collections` — tells a
    /// client something false, and a client that believes it stops asking. 503 is the one status
    /// that means *ask again*, and a proxy and a monitor both act on it.
    /// </remarks>
    /// <param name="detail">What could not be read.</param>
    /// <returns>The problem.</returns>
    public static OgcProblem Unavailable(string detail) =>
        new(503, "Service Unavailable", detail);

    /// <summary>The requested representation is not one this server writes.</summary>
    /// <param name="detail">What was asked for.</param>
    /// <returns>The problem.</returns>
    public static OgcProblem NotAcceptable(string detail) => new(406, "Not Acceptable", detail);

    /// <summary>The row is no longer at the version the caller said it expected.</summary>
    /// <remarks>
    /// <para>
    /// <b>412 rather than 409, and the difference is which header was disbelieved —
    /// D-186.</b> RFC 9110 §15.5.13 gives 412 exactly one meaning: a precondition the client
    /// stated in a header did not hold. A client that sent <c>If-Match</c> can act on that
    /// without reading a sentence — re-read the feature, re-apply the edit, send the new tag.
    /// 409 would say the request conflicts with the resource's state and leave the client to
    /// guess what to do about it.
    /// </para>
    /// <para>
    /// <b>The detail names the fix, not the fault.</b> The client did what it was asked to do;
    /// it is out of date, which is a thing it can only learn from here.
    /// </para>
    /// </remarks>
    /// <param name="detail">Which feature moved.</param>
    /// <returns>The problem.</returns>
    public static OgcProblem PreconditionFailed(string detail) =>
        new(412, "Precondition Failed", detail);

    /// <summary>The document.</summary>
    /// <returns>The JSON.</returns>
    public string ToJson()
    {
        using System.IO.MemoryStream stream = new();

        using (Utf8JsonWriter json = new(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();

            // <b>`type` is required and `about:blank` is its defined default</b> for a
            // problem whose type adds nothing beyond the status code. Inventing a URI
            // that resolves to nothing would be worse: a client that dereferences it
            // gets a second error while diagnosing the first.
            json.WriteString("type", "about:blank");
            json.WriteString("title", Title);
            json.WriteNumber("status", Status);
            json.WriteString("detail", Detail);
            json.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
