using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>Writes a JSON answer indented, for <c>f=pjson</c>.</summary>
internal static class PrettyJson
{
    private static readonly JsonWriterOptions Indented = new() { Indented = true };

    /// <summary>Buffers the answer and, when it is JSON, writes it again indented; anything else passes unchanged.</summary>
    public static async Task IndentAsync(HttpContext context, RequestDelegate next)
    {
        Stream original = context.Response.Body;
        using MemoryStream buffered = new();
        context.Response.Body = buffered;

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = original;
        }

        buffered.Position = 0;

        if (context.Response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true && buffered.Length > 0)
        {
            try
            {
                using JsonDocument document = await JsonDocument.ParseAsync(buffered).ConfigureAwait(false);
                using MemoryStream pretty = new();
                await using (Utf8JsonWriter writer = new(pretty, Indented))
                {
                    document.WriteTo(writer);
                }

                context.Response.ContentLength = pretty.Length;
                pretty.Position = 0;
                await pretty.CopyToAsync(original).ConfigureAwait(false);
                return;
            }
            catch (JsonException)
            {
                buffered.Position = 0;
            }
        }

        await buffered.CopyToAsync(original).ConfigureAwait(false);
    }
}
