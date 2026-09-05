using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A credential sent in the URL does not reach the server's log.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-015](../../docs/adr/ADR-015-authentication.md) condition 2, in the form it
/// asks for: *"tested by asserting on log output, not by reading the code. §4.1 fails
/// silently otherwise, and silently is the only way it fails."*</b>
/// </para>
/// <para>
/// <b>It was owed on 2026-08-20 and skipped.</b> The ArcGIS token endpoints and the
/// <c>token=</c> query parameter shipped that morning; the condition says the
/// redaction *"becomes due in the same change that adds `/generateToken`, not
/// before"*. The security gate then found live root session tokens in the log, took
/// one, and used it to read a private layer.
/// </para>
/// <para>
/// <b>Why the log path comes from the environment.</b> Where a deployment writes its
/// log is the operator's choice, so this reads <c>GRATICULA_TEST_LOG</c> and fails
/// loudly when it is unset rather than passing quietly — a test that skips itself is
/// the same silence this condition exists to break.
/// </para>
/// </remarks>
public sealed class TokenIsNotLoggedTests : ArcGisClient
{
    /// <summary>Where the server under test writes its log.</summary>
    public const string LogVariable = "GRATICULA_TEST_LOG";

    [Fact]
    public async Task A_token_in_the_query_string_is_redacted_before_it_is_written_down()
    {
        string? log = Environment.GetEnvironmentVariable(LogVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(log),
            $"{LogVariable} is not set, so this cannot read what the server wrote. ADR-015 "
            + "condition 2 requires the redaction to be asserted on log output; skipping quietly "
            + "would be the silent failure the condition names.");

        Assert.True(File.Exists(log), $"{LogVariable} points at {log}, which does not exist.");

        // <b>Where the log ends before the request is made.</b> Reading a fixed tail
        // instead failed in a full suite run: ten suites write faster than the window
        // is wide, so the line this test had just caused was already off the end of
        // it. An offset taken first is immune to volume.
        long from = new FileInfo(log!).Length;

        // <b>A sentinel rather than a real token</b>, so a failure prints something
        // that is not a credential and so the search cannot match an older line.
        string sentinel = "SENTINEL" + Guid.NewGuid().ToString("N");

        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(
            HttpMethod.Get, new Uri($"{root}/rest/info?f=json&token={sentinel}"));

        using HttpResponseMessage response = await Http.SendAsync(request);

        // The request has to have been served for the log line to exist at all.
        Assert.True(response.IsSuccessStatusCode, $"/rest/info answered {(int)response.StatusCode}.");

        // The writer flushes on its own schedule, and a short poll beats one long wait.
        string written = string.Empty;

        for (int attempt = 0; attempt < 20; attempt++)
        {
            written = await ReadFromAsync(log!, from);

            // <b>The request line, not any line that mentions the path.</b> This waited for
            // `/rest/info` and the routing middleware logs `Executing endpoint 'HTTP: GET
            // /rest/info'` first — measured 26 lines before the request line in a real CI log,
            // and more than that once Kestrel is at Debug. The poll broke on the endpoint line
            // and the assertions then ran against a log the redacted line had not reached yet,
            // which CI reported on 2026-09-05 as *token=REDACTED not found* against a server
            // that had redacted it correctly.
            //
            // <b>The question mark is what separates them.</b> The endpoint lines carry the
            // route template and no query; only the request line carries the query, which is
            // the thing this test is about.
            if (written.Contains("/rest/info?", StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(250);
        }

        Assert.DoesNotContain(sentinel, written, StringComparison.Ordinal);

        // <b>And the line exists.</b> Without this the test passes against a server
        // that logs nothing at all, which would satisfy the letter of the condition
        // and none of its purpose: an operator with no request log cannot answer
        // whether a credential was ever written.
        Assert.Contains("/rest/info", written, StringComparison.Ordinal);
        Assert.Contains("token=REDACTED", written, StringComparison.Ordinal);
    }

    /// <summary>Reads what a file has grown by since an offset.</summary>
    /// <remarks>
    /// <b>Opened with every share flag the writer needs.</b> The server has this file
    /// open and is appending to it; a reader that asked for exclusive access would
    /// fail rather than read.
    /// </remarks>
    private static async Task<string> ReadFromAsync(string path, long from)
    {
        await using FileStream file = new(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        if (from > file.Length)
        {
            // The file was replaced under us — a restart truncates it — so everything
            // in it is newer than the offset.
            from = 0;
        }

        // Seek before the reader is built, not after: a StreamReader samples the
        // stream on construction, so seeking underneath an existing one reads from
        // wherever that sampling left off.
        file.Seek(from, SeekOrigin.Begin);

        using StreamReader reader = new(file, detectEncodingFromByteOrderMarks: false);

        return await reader.ReadToEndAsync();
    }
}
