namespace SearchService.Infrastructure.Elasticsearch;

public class ElasticsearchOptions
{
    public string Uri { get; set; } = null!;

    public string IndexName { get; set; } = "search-entries";
}
