using Shared;

namespace Shared.UnitTests;

// Normalize is the single upper bound on how much any paged endpoint will return, and it
// is reached from six different controllers. Everything it guarantees is asserted here so
// a change to the clamp fails loudly rather than quietly widening every endpoint at once.
public class PagedResponseTests
{
    [Theory]
    [InlineData(null, PagedResponse<object>.DefaultSize)]
    [InlineData(1, 1)]
    [InlineData(PagedResponse<object>.MaxSize, PagedResponse<object>.MaxSize)]
    public void A_size_within_range_is_kept(int? requested, int expected) =>
        Assert.Equal(expected, PagedResponse<object>.Normalize(null, requested).Size);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_size_below_range_becomes_one_rather_than_a_negative_sql_limit(int requested) =>
        Assert.Equal(1, PagedResponse<object>.Normalize(null, requested).Size);

    [Theory]
    [InlineData(PagedResponse<object>.MaxSize + 1)]
    [InlineData(100_000)]
    [InlineData(int.MaxValue)]
    public void A_size_above_range_is_capped(int requested) =>
        Assert.Equal(PagedResponse<object>.MaxSize, PagedResponse<object>.Normalize(null, requested).Size);

    [Theory]
    [InlineData(null, 1)]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    public void Pages_start_at_one(int? requested, int expected) =>
        Assert.Equal(expected, PagedResponse<object>.Normalize(requested, null).Page);

    [Fact]
    public void Has_next_is_false_on_the_last_page()
    {
        var page = new PagedResponse<int>([1, 2], Page: 5, Size: 20, Total: 100);

        Assert.False(page.HasNext);
    }

    [Fact]
    public void Has_next_is_true_while_rows_remain()
    {
        var page = new PagedResponse<int>([1, 2], Page: 4, Size: 20, Total: 100);

        Assert.True(page.HasNext);
    }

    [Fact]
    public void Has_next_does_not_overflow_on_a_far_page()
    {
        // HasNext widens page * size to long on purpose; without that the product
        // overflows here and goes negative, which would report "there is more" forever.
        var page = new PagedResponse<int>([], Page: int.MaxValue, Size: PagedResponse<int>.MaxSize, Total: 100);

        Assert.False(page.HasNext);
    }

    [Fact]
    public void An_empty_page_still_reports_the_paging_it_was_asked_for()
    {
        var page = PagedResponse<int>.Empty(page: 3, size: 25);

        Assert.Empty(page.Items);
        Assert.Equal(3, page.Page);
        Assert.Equal(25, page.Size);
        Assert.Equal(0, page.Total);
        Assert.False(page.HasNext);
    }
}
