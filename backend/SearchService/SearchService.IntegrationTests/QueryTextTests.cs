using SearchService.Domain;

namespace SearchService.IntegrationTests;

public class QueryTextTests
{
    [Theory]
    [InlineData("shipping goods to buyers", "shipping goods buyers")]
    [InlineData("people who pay our suppliers", "people pay suppliers")]
    [InlineData("Research and Development", "Research Development")]
    [InlineData("  keeping   the servers running ", "keeping servers running")]
    public void Words_that_only_join_a_sentence_are_dropped_and_the_rest_keep_their_order_and_spelling(string query, string expected)
    {
        Assert.Equal(expected, QueryText.ForKeywordSearch(query));
    }

    [Theory]
    [InlineData("and")]
    [InlineData("the of and")]
    [InlineData("a")]
    public void A_query_made_only_of_such_words_is_left_as_typed_so_it_still_means_something(string query)
    {
        Assert.Equal(query, QueryText.ForKeywordSearch(query));
    }

    [Theory]
    [InlineData("payroll")]
    [InlineData("marketing team")]
    [InlineData("data science")]
    public void A_query_without_such_words_is_unchanged(string query)
    {
        Assert.Equal(query, QueryText.ForKeywordSearch(query));
    }
}
