using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// <b>Where an <c>f=json</c> export's picture waits for its client</b> — D-299: ArcGIS's output directory, in memory.
/// </summary>
/// <remarks>
/// <para>
/// <b>The <c>href</c> used to be the same request with <c>f=image</c></b>, which carried a <c>token=</c> from the query
/// string into a document a client may log, cache or pass on. ArcGIS answers an address under its output directory
/// that needs no token; this does the same. The picture is already drawn when the JSON is written — both exports draw
/// before they answer — so it is kept here under 128 random bits and the address is the capability: whoever holds it
/// may fetch that one picture, for a few minutes, and nothing else.
/// </para>
/// <para>
/// <b>Bounded three ways</b>: ten minutes (ArcGIS's own output cleanup), <see cref="MaximumPictures"/> pictures and
/// <see cref="MaximumBytes"/> in all, the oldest forgotten first. One process: a deployment of several servers must
/// send the client back to the one that drew it, as with <c>uploads/upload</c>.
/// </para>
/// </remarks>
internal static class ExportOutputs
{
    /// <summary>How long a picture waits.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>The most pictures kept at once.</summary>
    public const int MaximumPictures = 1024;

    /// <summary>The most bytes kept at once.</summary>
    public const long MaximumBytes = 256L * 1024 * 1024;

    /// <summary>The address under which pictures are served.</summary>
    public const string Path = "/rest/directories/arcgisoutput";

    private sealed record Picture(byte[] Bytes, string ContentType, DateTimeOffset Until);

    private static readonly ConcurrentDictionary<string, Picture> Pictures = new(StringComparer.Ordinal);

    /// <summary>Keeps a drawn picture and answers the address a client fetches it from.</summary>
    public static string Keep(HttpContext context, byte[] image, string contentType)
    {
        Sweep(image.LongLength);

        string name = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()
            + contentType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/tiff" => ".tif",
                _ => ".bin",
            };
        Pictures[name] = new Picture(image, contentType, DateTimeOffset.UtcNow + Lifetime);

        return $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{Path}/{name}";
    }

    /// <summary>The output directory's one route: a picture by its name, to anybody holding the name.</summary>
    public static void Map(WebApplication app) =>
        app.MapMethods($"{Path}/{{name}}", ["GET", "HEAD"], (string name) =>
                Pictures.TryGetValue(name, out Picture? picture) && picture.Until > DateTimeOffset.UtcNow
                    ? Results.File(picture.Bytes, picture.ContentType)
                    : Results.NotFound())
            .Governed(SharingGovernedExtensions.Public);

    /// <summary>Forgets what has expired, then the oldest until one more picture of this size fits.</summary>
    private static void Sweep(long incoming)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach ((string name, Picture picture) in Pictures)
        {
            if (picture.Until <= now)
            {
                Pictures.TryRemove(name, out _);
            }
        }

        long bytes = Pictures.Values.Sum(p => p.Bytes.LongLength);

        foreach ((string name, Picture picture) in Pictures.OrderBy(p => p.Value.Until).ToList())
        {
            if (Pictures.Count < MaximumPictures && bytes + incoming <= MaximumBytes)
            {
                break;
            }

            if (Pictures.TryRemove(name, out _))
            {
                bytes -= picture.Bytes.LongLength;
            }
        }
    }
}
