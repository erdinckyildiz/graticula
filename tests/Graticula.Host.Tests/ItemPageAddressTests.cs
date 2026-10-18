using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>ArcGIS's <c>home/item.html?id=</c> goes to Studio's item page, and nothing else does.</summary>
public sealed class ItemPageAddressTests
{
    private static string? Location(string path, params (string Key, string Value)[] query)
    {
        Dictionary<string, StringValues> values = [];
        foreach ((string key, string value) in query)
        {
            values[key] = value;
        }

        return ItemPageAddress.Location(path, new QueryCollection(values));
    }

    [Theory]
    [InlineData("/home/item.html")]
    [InlineData("/portal/home/item.html")]
    [InlineData("/studio/home/item.html")]
    [InlineData("/Portal/Home/Item.html")]
    public void Each_of_the_portal_s_addresses_goes_to_Studio_s_item_page(string path) =>
        Assert.Equal("/studio/item/0123456789abcdef0123456789abcdef", Location(path, ("id", "0123456789abcdef0123456789abcdef")));

    [Fact]
    public void The_other_parameters_go_with_it() =>
        Assert.Equal("/studio/item/abc?view=settings", Location("/home/item.html", ("id", "abc"), ("view", "settings")));

    [Theory]
    [InlineData("")]
    [InlineData("../../admin")]
    [InlineData("//evil.example")]
    [InlineData("https://evil.example")]
    [InlineData("abc\r\nSet-Cookie:x")]
    public void An_id_that_could_be_a_path_or_a_scheme_is_not_sent_anywhere(string id) =>
        Assert.Null(Location("/home/item.html", ("id", id)));

    [Theory]
    [InlineData("/home/items.html")]
    [InlineData("/studio/item/abc")]
    [InlineData("/home/item.html.bak")]
    public void Other_addresses_are_not_item_pages(string path) =>
        Assert.Null(Location(path, ("id", "abc")));
}
