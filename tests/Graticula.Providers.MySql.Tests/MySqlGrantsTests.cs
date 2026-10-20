using Xunit;

namespace Graticula.Providers.MySql.Tests;

/// <summary>
/// Whether a MySQL layer is editable, read from <c>SHOW GRANTS</c> as the server writes it — the forms measured on 8.4,
/// including the official image's escaped database pattern (found by CI) and privileges that come through a role.
/// </summary>
public sealed class MySqlGrantsTests
{
    [Theory]
    [InlineData("GRANT ALL PRIVILEGES ON `graticula\\_test`.* TO `graticula`@`%`", true)]
    [InlineData("GRANT ALL PRIVILEGES ON `graticula_test`.* TO `graticula`@`%`", true)]
    [InlineData("GRANT SELECT, INSERT, UPDATE, DELETE ON `graticula%`.* TO `zz_r`@`%`", true)]
    [InlineData("GRANT SELECT, INSERT, UPDATE, DELETE ON *.* TO `root`@`%`", true)]
    [InlineData("GRANT INSERT, UPDATE, DELETE ON `graticula_test`.`roads` TO `u`@`%`", true)]
    [InlineData("GRANT INSERT, UPDATE, DELETE ON TABLE `graticula_test`.`roads` TO `u`@`%`", true)]
    [InlineData("GRANT SELECT, INSERT, UPDATE ON `graticula_test`.* TO `u`@`%`", false)]
    [InlineData("GRANT ALL PRIVILEGES ON `other\\_db`.* TO `u`@`%`", false)]
    [InlineData("GRANT ALL PRIVILEGES ON `graticula\\_test`.`rivers` TO `u`@`%`", false)]
    [InlineData("GRANT SELECT, INSERT, UPDATE (`label`), DELETE ON `graticula_test`.`roads` TO `u`@`%`", false)]
    [InlineData("GRANT `writer`@`%` TO `u`@`%`", false)]

    // A session in ANSI mode, which every session of the provider is, is answered in double quotes (measured on 8.4).
    [InlineData("GRANT ALL PRIVILEGES ON \"graticula\\_test\".* TO \"graticula\"@\"%\"", true)]
    [InlineData("GRANT INSERT, UPDATE, DELETE ON \"graticula_test\".\"roads\" TO \"u\"@\"%\"", true)]
    [InlineData("GRANT INSERT, UPDATE, DELETE ON \"graticula_test\".\"rivers\" TO \"u\"@\"%\"", false)]
    public void One_grant_line_is_read_as_MySQL_applies_it(string grant, bool writes) =>
        Assert.Equal(writes, MySqlConnections.Writes([grant], "graticula_test", "roads"));

    [Fact]
    public void An_escaped_underscore_is_a_literal_and_a_bare_one_is_a_wildcard()
    {
        Assert.False(MySqlConnections.Writes(["GRANT ALL ON `graticula\\_test`.* TO `u`@`%`"], "graticulaXtest", "roads"));
        Assert.True(MySqlConnections.Writes(["GRANT ALL ON `graticula_test`.* TO `u`@`%`"], "graticulaXtest", "roads"));
    }

    [Fact]
    public void The_three_rights_may_come_from_different_lines()
    {
        Assert.True(MySqlConnections.Writes(
            [
                "GRANT USAGE ON *.* TO `u`@`%`",
                "GRANT INSERT ON `graticula\\_test`.* TO `u`@`%`",
                "GRANT UPDATE, DELETE ON `graticula_test`.`roads` TO `u`@`%`",
                "GRANT `writer`@`%` TO `u`@`%`",
            ],
            "graticula_test",
            "roads"));
    }
}
