using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Graticula.Platform.Webhooks;

namespace Graticula.Host;

/// <summary>
/// What a request to make or change a webhook says, read and checked the same way at both doors — ADR-266 §1.
/// </summary>
internal static partial class WebhookRequests
{
    /// <summary>The longest secret kept.</summary>
    internal const int SecretLimit = 256;

    /// <summary>The largest <c>config.properties</c> kept, in characters of JSON.</summary>
    internal const int PropertiesLimit = 4096;

    [GeneratedRegex("^[A-Za-z0-9_.@-]{1,128}$")]
    private static partial Regex IdSegment();

    /// <summary>
    /// The portal event URIs a webhook names, checked against ArcGIS's list and written as this server raises them: an
    /// item's or a group's id without hyphens and in lower case.
    /// </summary>
    /// <param name="text">A comma-separated list, or a JSON array.</param>
    /// <param name="events">The URIs.</param>
    /// <param name="error">Why not.</param>
    /// <returns>Whether every one is an event.</returns>
    internal static bool TryReadPortalEvents(string? text, out string[] events, out string? error)
    {
        events = [];
        IEnumerable<string> given;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "'events' names no events; give event URIs such as /items/add, or changes=allChanges.";
            return false;
        }

        if (text.TrimStart().StartsWith('['))
        {
            try
            {
                given = JsonSerializer.Deserialize<string[]>(text) ?? [];
            }
            catch (JsonException)
            {
                error = "'events' is not a JSON array of event URIs.";
                return false;
            }
        }
        else
        {
            given = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        List<string> read = [];

        foreach (string one in given)
        {
            if (Normalised(one) is not { } uri)
            {
                error = $"'{one}' is not an event this portal knows. An event is /, /items, /groups, /users or /roles, "
                    + "an operation under one (/items/add), an id under one (/items/<id>), or an operation of one id "
                    + "(/items/<id>/update).";
                return false;
            }

            if (!read.Contains(uri, StringComparer.Ordinal))
            {
                read.Add(uri);
            }
        }

        if (read.Count is 0 or > 64)
        {
            error = "'events' names between one and sixty-four events.";
            return false;
        }

        // ADR-266 condition 1: a webhook that could never fire is refused at its creation, with the reason, rather than
        // accepted and left silent — sign-in and sign-out, an invitation, a join request, a move.
        if (read.All(NeverRaised))
        {
            error = $"This server never raises {string.Join(", ", read)}, so a webhook on only "
                + (read.Count == 1 ? "that event" : "those events")
                + " would never be called. Sign-in and sign-out are not raised: they are an authentication log, not an act "
                + "on content (ADR-266 §5.2).";
            return false;
        }

        events = [.. read];
        error = null;
        return true;
    }

    /// <summary>Whether a normalised event names only an operation no audited act raises.</summary>
    private static bool NeverRaised(string uri)
    {
        string[] parts = uri.Split('/');

        // "/", "/items" and "/items/<id>" are raised by every operation under them.
        string? operation = parts.Length switch
        {
            3 when WebhookEvents.PortalOperations.TryGetValue(parts[1], out string[]? ops) && ops.Contains(parts[2], StringComparer.Ordinal) => parts[2],
            4 => parts[3],
            _ => null,
        };

        if (operation is null)
        {
            return false;
        }

        string source = parts[1].TrimEnd('s');
        return !WebhookEvents.PortalActs.Values.Any(act => act.Source == source && act.Operation == operation);
    }

    private static string? Normalised(string uri)
    {
        string[] parts = uri.Trim().TrimEnd('/').Split('/');

        if (uri.Trim() == "/")
        {
            return "/";
        }

        if (parts.Length < 2 || parts[0].Length != 0 || !WebhookEvents.PortalOperations.TryGetValue(parts[1], out string[]? operations))
        {
            return null;
        }

        string source = parts[1];

        bool IsOperation(string segment) => operations.Contains(segment, StringComparer.Ordinal);

        // An item's and a group's id is ArcGIS's 32 hexadecimal digits, so a misspelt operation is not read as one; a
        // user is named by their username.
        string? Id(string segment) => source switch
        {
            "items" or "groups" => Guid.TryParse(segment, out Guid guid) ? guid.ToString("N") : null,
            "users" when IdSegment().IsMatch(segment) => segment,
            _ => null,
        };

        return parts.Length switch
        {
            2 => $"/{source}",
            3 when IsOperation(parts[2]) => $"/{source}/{parts[2]}",
            3 when Id(parts[2]) is { } id => $"/{source}/{id}",
            4 when Id(parts[2]) is { } id && IsOperation(parts[3]) => $"/{source}/{id}/{parts[3]}",
            _ => null,
        };
    }

    /// <summary>A feature service webhook's change types, checked against ArcGIS's list; <c>*</c> when none is given.</summary>
    /// <param name="text">A JSON array or a comma-separated list, or nothing.</param>
    /// <param name="types">The types.</param>
    /// <param name="error">Why not.</param>
    /// <returns>Whether every one is a change type.</returns>
    internal static bool TryReadChangeTypes(string? text, out string[] types, out string? error)
    {
        types = ["*"];
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        string[] given;

        try
        {
            given = text.TrimStart().StartsWith('[')
                ? JsonSerializer.Deserialize<string[]>(text) ?? []
                : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (JsonException)
        {
            error = "'changeTypes' is not a JSON array of change types.";
            return false;
        }

        List<string> read = [];

        foreach (string one in given)
        {
            // ArcGIS's own listing writes "All" for the wildcard.
            string type = one == "All" ? "*" : one;
            string? known = WebhookEvents.ChangeTypes.FirstOrDefault(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase));

            if (known is null)
            {
                error = $"'{one}' is not a change type. A change type is one of {string.Join(", ", WebhookEvents.ChangeTypes)}.";
                return false;
            }

            if (!read.Contains(known))
            {
                read.Add(known);
            }
        }

        // ADR-266 condition 1: only change types this server never raises would make a webhook that is never called.
        if (read.Count > 0 && read.All(type => type != "*" && !WebhookEvents.ChangeTypesRaised.Contains(type, StringComparer.Ordinal)))
        {
            error = $"This server never raises {string.Join(", ", read)}: a layer's definition is changed here only through "
                + "its fields (LayerSchemaChanged) or the service's (FeatureServiceDefinitionChanged), so a webhook on only "
                + "that would never be called (ADR-266).";
            return false;
        }

        types = read.Count == 0 ? ["*"] : [.. read];
        return true;
    }

    /// <summary>A portal webhook's <c>config</c>: its deactivation policy and its properties.</summary>
    /// <param name="text">The JSON, or nothing.</param>
    /// <param name="failures">The policy's <c>numberOfFailures</c>, or null when not given.</param>
    /// <param name="days">The policy's <c>daysInPast</c>, or null when not given.</param>
    /// <param name="properties">The <c>properties</c> object as JSON, or null when not given.</param>
    /// <param name="error">Why not.</param>
    /// <returns>Whether it reads.</returns>
    internal static bool TryReadConfig(string? text, out int? failures, out int? days, out string? properties, out string? error)
    {
        failures = null;
        days = null;
        properties = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        JsonObject config;

        try
        {
            if (JsonNode.Parse(text) is not JsonObject parsed)
            {
                error = "'config' is a JSON object.";
                return false;
            }

            config = parsed;
        }
        catch (JsonException)
        {
            error = "'config' is not valid JSON.";
            return false;
        }

        if (config["deactivationPolicy"] is JsonObject policy)
        {
            if (!TryNumber(policy["numberOfFailures"], 1, 1000, out failures)
                || !TryNumber(policy["daysInPast"], 1, 365, out days))
            {
                error = "'deactivationPolicy' takes numberOfFailures from 1 to 1000 and daysInPast from 1 to 365.";
                return false;
            }
        }

        if (config["properties"] is JsonObject given)
        {
            properties = given.ToJsonString();

            if (properties.Length > PropertiesLimit)
            {
                error = $"'config.properties' is larger than {PropertiesLimit} characters of JSON.";
                return false;
            }
        }

        return true;
    }

    private static bool TryNumber(JsonNode? node, int least, int most, out int? number)
    {
        number = null;

        if (node is null)
        {
            return true;
        }

        if (node is JsonValue value && (value.TryGetValue(out int n) || (value.TryGetValue(out string? s) && int.TryParse(s, out n)))
            && n >= least && n <= most)
        {
            number = n;
            return true;
        }

        return false;
    }

    /// <summary>A <c>contentType</c> parameter, or the default; null when it is not one of the two.</summary>
    /// <param name="text">The parameter.</param>
    /// <returns>The media type, or null.</returns>
    internal static string? ContentType(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "application/json"
        : text.Trim().ToLowerInvariant() is "application/json" or "application/x-www-form-urlencoded" ? text.Trim().ToLowerInvariant()
        : null;

    /// <summary>A <c>payloadFormat</c> parameter, or the default; null when it is not one of the two.</summary>
    /// <param name="text">The parameter.</param>
    /// <returns>The format, or null.</returns>
    internal static string? PayloadFormat(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "json"
        : text.Trim().ToLowerInvariant() is "json" or "pjson" ? text.Trim().ToLowerInvariant()
        : null;

    /// <summary>The host of a webhook's URL, which is what its audit row records — never the URL, whose query may be a key.</summary>
    /// <param name="url">The URL.</param>
    /// <returns>The host.</returns>
    internal static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) ? parsed.Host : string.Empty;

    /// <summary>The time a webhook's document says, in milliseconds since the epoch.</summary>
    /// <param name="when">The moment.</param>
    /// <returns>The milliseconds.</returns>
    internal static long Millis(DateTimeOffset when) => when.ToUnixTimeMilliseconds();

    /// <summary>A webhook's recent deliveries, as both doors list them.</summary>
    /// <param name="recent">The deliveries.</param>
    /// <returns>The objects.</returns>
    internal static object[] Deliveries(IReadOnlyList<WebhookDeliveryRecord> recent) =>
    [
        .. recent.Select(d => new
        {
            notificationId = d.Id.ToString("N"),
            @event = d.Event,
            status = d.Status,
            statusCode = d.LastStatus,
            attempts = d.Attempts,
            error = d.LastError,
            created = d.Created.ToUnixTimeMilliseconds(),
            completed = d.Finished?.ToUnixTimeMilliseconds(),
        }),
    ];
}
