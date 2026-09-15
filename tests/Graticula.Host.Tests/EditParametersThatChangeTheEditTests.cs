using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// An edit parameter that would change what is written is refused rather than dropped.
/// </summary>
/// <remarks>
/// Written 2026-09-15. <c>useGlobalIds=true</c> and <c>attachments</c> were accepted by
/// <c>applyEdits</c> and ignored, so the caller was told a different edit had succeeded.
/// </remarks>
public sealed class EditParametersThatChangeTheEditTests
{
    private static string? Refusal(params (string Key, string Value)[] fields) => Refusal(false, fields);

    private static string? Refusal(bool globalIdsHonoured, params (string Key, string Value)[] fields)
    {
        Dictionary<string, StringValues> values = [];

        foreach ((string key, string value) in fields)
        {
            values[key] = value;
        }

        return Program.EditParameterRefusal(new FormCollection(values), new DefaultHttpContext(), globalIdsHonoured);
    }

    /// <summary>
    /// <c>useGlobalIds=true</c> is honoured by applyEdits since 2026-09-15 and refused by the single
    /// operations, which address features by object id.
    /// </summary>
    [Fact]
    public void Matching_by_GlobalID_is_applyEdits_own_and_refused_elsewhere()
    {
        Assert.Contains("applyEdits", Refusal(("useGlobalIds", "true")), System.StringComparison.Ordinal);
        Assert.Null(Refusal(true, ("useGlobalIds", "true")));
    }

    [Fact]
    public void Attachments_in_the_edit_are_refused_and_the_refusal_says_where_they_go()
    {
        Assert.Contains(
            "addAttachment",
            Refusal(("attachments", """{"adds":[{"uploadId":"x","parentGlobalId":"{1}"}]}""")),
            System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("useGlobalIds", "false")]
    [InlineData("attachments", "{}")]
    [InlineData("gdbVersion", "sde.DEFAULT")]
    [InlineData("sessionID", "abc")]
    [InlineData("returnEditMoment", "true")]
    public void What_asks_for_nothing_the_edit_does_not_already_do_is_accepted(string key, string value)
    {
        Assert.Null(Refusal((key, value)));
    }
}
