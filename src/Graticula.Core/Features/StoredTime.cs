using System;

namespace Graticula.Features;

/// <summary>
/// A moment read from a store, as the UTC instant every face writes it as.
/// </summary>
/// <remarks>
/// <para>
/// <b>A value with no zone is UTC, not this machine's local time — 2026-10-16.</b> A <c>timestamp</c> or <c>date</c>
/// column comes back from the provider with <see cref="DateTimeKind.Unspecified"/>, and the faces called
/// <see cref="DateTime.ToUniversalTime"/> on it, which reads an unspecified value as local: on a server running at
/// +03:00 every such value went out three hours early and a date became the evening before. Every layer document says
/// <c>dateFieldsTimeReference: UTC</c>, DuckDB's sessions run in UTC (ADR-066), and a server in a container never saw
/// the difference — ArcGIS Pro's typed edit against a server on a Turkish desktop did.
/// </para>
/// <para>
/// The other half is the session: a PostGIS connection that reads layers runs with <c>TimeZone</c> UTC, so a client's
/// instant is written into a <c>timestamp</c> column as the UTC wall clock this reads back.
/// </para>
/// </remarks>
public static class StoredTime
{
    /// <summary>The instant, in UTC: an unspecified value taken as UTC, a local one converted.</summary>
    /// <param name="moment">A value read from a store.</param>
    /// <returns>The same instant, with <see cref="DateTimeKind.Utc"/>.</returns>
    public static DateTime Utc(DateTime moment) => moment.Kind switch
    {
        DateTimeKind.Unspecified => DateTime.SpecifyKind(moment, DateTimeKind.Utc),
        DateTimeKind.Local => moment.ToUniversalTime(),
        _ => moment,
    };
}
