using System.Collections.Generic;

namespace Graticula.Platform.Admin;

/// <summary>
/// What a service's owner decides about it beside its sharing — ADR-102, owner decision 2026-10-01.
/// </summary>
/// <param name="EditingOffered">The edits the owner offers, or null when the owner has not chosen (the ceiling alone).</param>
/// <param name="DeleteProtected">Whether the service refuses to be deleted until this is turned off.</param>
/// <param name="Ceiling">The administrator's capability ceiling, or null for every operation; the owner chooses inside it.</param>
/// <param name="OgcOff">The OGC faces the owner turned off (ADR-166).</param>
public sealed record ServiceStewardship(
    IReadOnlyList<string>? EditingOffered,
    bool DeleteProtected,
    IReadOnlyList<string>? Ceiling,
    IReadOnlyList<string>? OgcOff = null);
