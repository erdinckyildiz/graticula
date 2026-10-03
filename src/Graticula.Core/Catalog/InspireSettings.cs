using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Graticula.Catalog;

/// <summary>
/// A service's INSPIRE settings — ADR-172, ArcGIS Server's INSPIRE View and Download service extensions: the link to
/// its metadata record, its language, and the data set its download service delivers.
/// </summary>
/// <param name="MetadataUrl">The service's metadata record in a discovery service (INSPIRE scenario 1).</param>
/// <param name="Language">Its language, as ISO 639-2/B — <c>eng</c>, <c>tur</c>, <c>ger</c>.</param>
/// <param name="DatasetCode">The data set's unique identifier code, which a Download service names, or null.</param>
/// <param name="DatasetNamespace">The identifier's namespace, or null.</param>
public sealed record InspireSettings(string MetadataUrl, string Language, string? DatasetCode, string? DatasetNamespace)
{
    /// <summary>
    /// The languages an INSPIRE service names — the EU's official languages and Turkish, as ISO 639-2/B, which INSPIRE
    /// uses rather than 639-2/T.
    /// </summary>
    public static readonly System.Collections.Generic.IReadOnlySet<string> Languages = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
    {
        "bul", "hrv", "cze", "dan", "dut", "eng", "est", "fin", "fre", "ger", "gre", "hun", "gle", "ita", "lav", "lit", "mlt",
        "pol", "por", "rum", "slo", "slv", "spa", "swe", "tur",
    };

    /// <summary>A language as INSPIRE spells it: lower case, and the 639-2/T codes that differ turned into their /B ones.</summary>
    /// <param name="language">As given.</param>
    /// <returns>The code.</returns>
    public static string Normalise(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        string code = language.Trim().ToLowerInvariant();
        return code switch
        {
            "deu" => "ger", "fra" => "fre", "nld" => "dut", "ces" => "cze", "ell" => "gre", "ron" => "rum", "slk" => "slo",
            _ => code,
        };
    }

    /// <summary>The longest any of the four may be.</summary>
    public const int MaximumLength = 2000;

    /// <summary>Whether the WFS face may declare a Download service: only with a data set to name.</summary>
    public bool Downloads => DatasetCode is { Length: > 0 };

    /// <summary>Reads the stored document, or null when there is none or it cannot be read.</summary>
    /// <param name="stored">The stored JSON.</param>
    /// <returns>The settings.</returns>
    public static InspireSettings? Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(stored) is JsonObject o && (string?)o["metadataUrl"] is { Length: > 0 } url
                ? new InspireSettings(url, (string?)o["language"] ?? "eng", (string?)o["datasetCode"], (string?)o["datasetNamespace"])
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>What is stored.</summary>
    /// <returns>The JSON.</returns>
    public string ToJson() => new JsonObject
    {
        ["metadataUrl"] = MetadataUrl,
        ["language"] = Language,
        ["datasetCode"] = DatasetCode,
        ["datasetNamespace"] = DatasetNamespace,
    }.ToJsonString();

    /// <summary>Why these settings cannot be stored, or null when they can.</summary>
    /// <returns>The reason.</returns>
    public string? Refusal()
    {
        if (!Uri.TryCreate(MetadataUrl, UriKind.Absolute, out Uri? url) || url.Scheme is not ("http" or "https"))
        {
            return "The metadata record is an http or https address — a CSW GetRecordById, as INSPIRE's scenario 1 asks.";
        }

        if (!Languages.Contains(Language))
        {
            return $"The language is a three-letter ISO 639-2/B code, such as eng, ger, fre or tur; `{Language}` is not one INSPIRE uses.";
        }

        if (MetadataUrl.Length > MaximumLength || (DatasetCode?.Length ?? 0) > MaximumLength || (DatasetNamespace?.Length ?? 0) > MaximumLength)
        {
            return $"Each is at most {MaximumLength:N0} characters.";
        }

        return DatasetNamespace is { Length: > 0 } && DatasetCode is not { Length: > 0 }
            ? "A namespace belongs to a data set's code; give the code too, or neither."
            : null;
    }
}
