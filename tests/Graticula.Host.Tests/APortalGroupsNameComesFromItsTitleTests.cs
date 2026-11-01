using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A group made through <c>community/createGroup</c> takes its name from its title, since a portal group has only a
/// title and ours needs both (PortalEndpoints.GroupWrites).
/// </summary>
public sealed class APortalGroupsNameComesFromItsTitleTests
{
    [Theory]
    [InlineData("Kontrol Group", "kontrol-group")]
    [InlineData("  Field crews — north  ", "field-crews-north")]
    [InlineData("Ölçüm ekibi Işık", "olcum-ekibi-isik")]
    [InlineData("Año Ýdun Øst", "ano-ydun-ost")]
    [InlineData("!!!", "group")]
    public void Letters_and_digits_are_kept_and_every_other_run_is_one_hyphen(string title, string name) =>
        Assert.Equal(name, PortalEndpoints.GroupNameOf(title));

    [Fact]
    public void A_long_title_makes_a_name_of_at_most_sixty_that_does_not_end_in_a_hyphen()
    {
        string made = PortalEndpoints.GroupNameOf(new string('a', 59) + " b" + new string('c', 30));

        Assert.True(made.Length <= 60, made);
        Assert.False(made.EndsWith('-'), made);
    }
}
