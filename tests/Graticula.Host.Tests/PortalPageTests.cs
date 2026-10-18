using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>A portal listing's page, by ArcGIS's start, num, sortField and sortOrder — V-83.</summary>
public sealed class PortalPageTests
{
    private sealed record Item(string Title, long Created);

    private static readonly IReadOnlyList<object> Five =
    [
        new Item("Delta", 4), new Item("alpha", 1), new Item("Echo", 5), new Item("Charlie", 3), new Item("bravo", 2),
    ];

    private static PortalPage.Page Page(string query)
    {
        DefaultHttpContext context = new();
        context.Request.QueryString = new QueryString(query);
        return PortalPage.Of(Five, context);
    }

    private static string[] Titles(PortalPage.Page page) => [.. page.Results.Cast<Item>().Select(i => i.Title)];

    [Fact]
    public void Num_and_start_cut_a_page_and_next_start_says_where_the_next_begins()
    {
        PortalPage.Page first = Page("?num=2");
        PortalPage.Page second = Page("?num=2&start=3");
        PortalPage.Page last = Page("?num=2&start=5");

        Assert.Equal((5, 1, 2, 3), (first.Total, first.Start, first.Num, first.NextStart));
        Assert.Equal((3, 2, 5), (second.Start, second.Num, second.NextStart));
        Assert.Equal((5, 1, -1), (last.Start, last.Num, last.NextStart));
    }

    [Fact]
    public void Without_num_ten_come_as_ArcGIS_answers_and_next_start_says_where_the_rest_begins()
    {
        IReadOnlyList<object> many = [.. Enumerable.Range(1, 25).Select(i => (object)new Item($"item {i:00}", i))];
        DefaultHttpContext context = new();

        PortalPage.Page page = PortalPage.Of(many, context);

        Assert.Equal((25, 1, 10, 11), (page.Total, page.Start, page.Num, page.NextStart));
        Assert.Equal((5, 1, 5, -1), (Page(string.Empty).Total, Page(string.Empty).Start, Page(string.Empty).Num, Page(string.Empty).NextStart));
    }

    [Fact]
    public void A_start_past_the_end_is_an_empty_page()
    {
        PortalPage.Page page = Page("?num=10&start=9");

        Assert.Empty(page.Results);
        Assert.Equal(-1, page.NextStart);
    }

    [Fact]
    public void Num_is_held_at_ArcGIS_s_ceiling() =>
        Assert.Equal(5, Page($"?num={PortalPage.MaximumNum + 50}").Num);

    [Fact]
    public void Sort_field_orders_case_insensitively_and_sort_order_reverses_it()
    {
        Assert.Equal(["alpha", "bravo", "Charlie", "Delta", "Echo"], Titles(Page("?sortField=title")));
        Assert.Equal(["Echo", "Delta", "Charlie", "bravo", "alpha"], Titles(Page("?sortField=title&sortOrder=desc")));
        Assert.Equal(["Echo", "Delta"], Titles(Page("?sortField=created&sortOrder=desc&num=2")));
    }

    [Fact]
    public void An_unknown_sort_field_leaves_the_order_as_it_was() =>
        Assert.Equal(["Delta", "alpha", "Echo", "Charlie", "bravo"], Titles(Page("?sortField=numviews")));
}
