using System;
using Graticula.Api.ArcGis;
using Xunit;

namespace Graticula.Api.ArcGis.Tests;

/// <summary>
/// What a service's named style may be called — ADR-094.
/// </summary>
/// <remarks>
/// <b>A name is a path segment first.</b> It is served as <c>resources/styles/{name}.json</c>, so a name
/// that needs escaping, holds a slash or a dot, or is <c>root</c> would be an address that does not mean
/// what it says. The store's check constraint repeats the same pattern; these pin the validator's side.
/// </remarks>
public sealed class StyleNamesTests
{
    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    [InlineData("Dark")]
    [InlineData("print_a4")]
    [InlineData("high-contrast")]
    [InlineData("2024")]
    [InlineData("default")]
    [InlineData("a")]
    public void A_short_slug_is_a_name(string name) =>
        Assert.True(StyleNames.TryValidate(name, out string? error), error);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-dark")]
    [InlineData("_dark")]
    [InlineData("dark mode")]
    [InlineData("dark.json")]
    [InlineData("dark/light")]
    [InlineData("..")]
    [InlineData("dark%20")]
    [InlineData("karanlık")]
    [InlineData("root")]
    [InlineData("ROOT")]
    [InlineData("Root")]
    public void Anything_else_is_refused(string? name) =>
        Assert.False(StyleNames.TryValidate(name, out _), $"'{name}' was accepted as a style name.");

    [Fact]
    public void A_name_is_at_most_forty_characters()
    {
        Assert.True(StyleNames.TryValidate(new string('a', StyleNames.MaximumLength), out _));
        Assert.False(StyleNames.TryValidate(new string('a', StyleNames.MaximumLength + 1), out string? error));
        Assert.Contains("40", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Root_is_refused_with_the_reason()
    {
        Assert.False(StyleNames.TryValidate("root", out string? error));
        Assert.Contains("root.json", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_names_are_one_style_when_they_differ_only_in_case()
    {
        Assert.True(StyleNames.Same("Dark", "dark"));
        Assert.False(StyleNames.Same("dark", "darker"));
        Assert.False(StyleNames.Same(null, "dark"));
    }
}
