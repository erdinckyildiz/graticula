using Xunit;

namespace Graticula.Host.Tests;

/// <summary>Which <c>callback</c> names are written around an answer — V-85.</summary>
public sealed class JsonpTests
{
    [Theory]
    [InlineData("cb")]
    [InlineData("dojo_request_script_callbacks.dojo_request_script0")]
    [InlineData("dojo.io.script.jsonp_dojoIoScript1._jsonpCallback")]
    [InlineData("$jsonp_1")]
    public void A_JavaScript_name_or_a_dotted_path_of_them_is_a_callback(string name) =>
        Assert.True(Jsonp.IsCallback(name));

    [Theory]
    [InlineData("")]
    [InlineData("alert(1)")]
    [InlineData("cb;alert(1)")]
    [InlineData("1cb")]
    [InlineData("a..b")]
    [InlineData("cb<script>")]
    [InlineData("cb\n")]
    public void Anything_that_could_be_more_than_a_name_is_not(string name) =>
        Assert.False(Jsonp.IsCallback(name));

    [Fact]
    public void A_name_longer_than_128_characters_is_not() =>
        Assert.False(Jsonp.IsCallback(new string('a', 129)));
}
