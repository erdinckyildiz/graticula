using Graticula.Host;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The redaction that lets a token travel in a query string.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-015](../../docs/adr/ADR-015-authentication.md) condition 2, which became
/// due on 2026-08-20 and was missed.</b> §4 accepts <c>token=</c> because unmodified
/// Esri clients send it there, under four mitigations of which redaction is the
/// first, and the condition says it *"becomes due in the same change that adds
/// `/generateToken`, not before"*. That change shipped without it, and the security
/// gate found live root tokens in the log and replayed one.
/// </para>
/// <para>
/// <b>These test the function; `TokenIsNotLoggedTests` tests the log.</b> The
/// condition asks for an assertion on log output, and it is right to — a correct
/// function that nothing calls redacts nothing.
/// </para>
/// </remarks>
public sealed class QueryRedactionTests
{
    [Theory]
    [InlineData("?f=json&token=abc123", "?f=json&token=REDACTED")]
    [InlineData("?token=abc123", "?token=REDACTED")]
    [InlineData("?token=abc123&f=json", "?token=REDACTED&f=json")]
    [InlineData("f=json&token=abc", "f=json&token=REDACTED")]
    public void A_token_value_is_replaced_and_the_parameter_is_kept(string query, string expected)
    {
        // <b>The parameter survives and only its value goes.</b> A line that dropped
        // it entirely would hide that the caller authenticated through the URL at
        // all, which is the thing an operator most wants to see when asking whether
        // their logs have ever held a credential.
        Assert.Equal(expected, QueryRedaction.Redact(query));
    }

    [Theory]
    [InlineData("?TOKEN=abc")]
    [InlineData("?Token=abc")]
    [InlineData("?ToKeN=abc")]
    public void Case_does_not_save_a_credential(string query)
    {
        // Every face on this server reads its parameters case-insensitively, so
        // `TOKEN=` authenticates. A case-sensitive redaction would log it in full.
        Assert.DoesNotContain("abc", QueryRedaction.Redact(query), System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("?password=hunter2")]
    [InlineData("?access_token=xyz")]
    [InlineData("?api_key=xyz")]
    [InlineData("?secret=xyz")]
    public void Every_named_credential_goes(string query)
    {
        Assert.Contains("REDACTED", QueryRedaction.Redact(query), System.StringComparison.Ordinal);
    }

    [Fact]
    public void Everything_else_survives_intact()
    {
        // <b>A redaction that ate the request is a log nobody can use.</b> The whole
        // value of the line is the path and the parameters that are not credentials.
        const string Query =
            "?service=WMS&request=GetMap&bbox=1,2,3,4&layers=tr_il&token=secret&format=image/png";

        string redacted = QueryRedaction.Redact(Query);

        Assert.Contains("service=WMS", redacted, System.StringComparison.Ordinal);
        Assert.Contains("bbox=1,2,3,4", redacted, System.StringComparison.Ordinal);
        Assert.Contains("format=image/png", redacted, System.StringComparison.Ordinal);
        Assert.DoesNotContain("secret", redacted, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("?", "?")]
    [InlineData("?flag", "?flag")]
    public void A_query_with_nothing_to_redact_is_returned_as_it_is(string? query, string expected)
    {
        Assert.Equal(expected, QueryRedaction.Redact(query));
    }

    [Fact]
    public void A_parameter_whose_name_merely_contains_a_credential_word_is_untouched()
    {
        // <b>Named, not pattern-matched.</b> A heuristic over names would redact a
        // layer called `password_zones` and still miss a credential called something
        // else. The list is short because the surface is.
        Assert.Equal(
            "?layers=password_zones&tokenizer=x",
            QueryRedaction.Redact("?layers=password_zones&tokenizer=x"));
    }
}
