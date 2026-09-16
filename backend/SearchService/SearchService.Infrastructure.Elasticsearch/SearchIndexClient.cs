using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Search;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Microsoft.Extensions.Options;
using SearchService.Domain;

namespace SearchService.Infrastructure.Elasticsearch;

// Owns the one index every kind is stored in. Title is mapped as search_as_you_type,
// which is what makes "exact match, prefix match, then token/substring match" (the
// issue's own ranking requirement) fall out of a single multi_match/bool_prefix query
// instead of hand-written ranking SQL - the _2gram/_3gram subfields it generates are
// exactly the prefix-matching machinery that requirement needs.
public class SearchIndexClient
{
    private readonly ElasticsearchClient _client;
    private readonly string _indexName;

    public SearchIndexClient(ElasticsearchClient client, IOptions<ElasticsearchOptions> options)
    {
        _client = client;
        _indexName = options.Value.IndexName;
    }

    public async Task EnsureIndexAsync(CancellationToken cancellationToken)
    {
        var exists = await _client.Indices.ExistsAsync(_indexName, cancellationToken);
        if (exists.Exists)
        {
            return;
        }

        var response = await _client.Indices.CreateAsync<SearchDocument>(
            _indexName,
            c => c.Mappings(m => m.Properties(p => p
                .Keyword(f => f.Kind)
                .Keyword(f => f.SourceId)
                .SearchAsYouType(f => f.Title)
                .Text(f => f.Subtitle)
                .Text(f => f.SearchText)
                .Boolean(f => f.IsActive)
                .Date(f => f.OccurredAt))),
            cancellationToken);

        if (!response.IsValidResponse)
        {
            throw new InvalidOperationException($"Elasticsearch index creation failed: {response.DebugInformation}");
        }
    }

    // Indexing with an explicit, deterministic Id is an upsert - see SearchDocument.Id's
    // own doc comment for why this is what makes Kafka redelivery safe here without a
    // separate "already processed" check.
    public async Task UpsertAsync(SearchDocument document, CancellationToken cancellationToken)
    {
        var response = await _client.IndexAsync(document, i => i.Index(_indexName).Id(document.Id), cancellationToken);
        if (!response.IsValidResponse)
        {
            throw new InvalidOperationException($"Elasticsearch index failed: {response.DebugInformation}");
        }
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken) =>
        _client.DeleteAsync(new DeleteRequest(_indexName, id), cancellationToken);

    // Used to resolve a department/position's own Title when indexing an employee doc -
    // the same "join against your own already-materialized data" principle as
    // NotificationService.AccountLookup, just backed by Elasticsearch's own get-by-id
    // instead of a Postgres table.
    public async Task<SearchDocument?> GetAsync(string id, CancellationToken cancellationToken)
    {
        var response = await _client.GetAsync<SearchDocument>(_indexName, id, cancellationToken);
        return response.Found ? response.Source : null;
    }

    // Elasticsearch's search API is only near-real-time (default 1s refresh interval) -
    // production code never needs this (a search landing a second before a write is
    // committed is an acceptable trade for not paying refresh's indexing-throughput cost
    // on every single document), but tests asserting on SearchAsync results right after an
    // Upsert need it to avoid flakiness.
    public Task RefreshAsync(CancellationToken cancellationToken) =>
        _client.Indices.RefreshAsync(_indexName, cancellationToken);

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string query, IReadOnlyCollection<string>? kinds, int limit, CancellationToken cancellationToken)
    {
        var response = await _client.SearchAsync<SearchDocument>(s => s
            .Indices(_indexName)
            .Size(limit)
            .Query(q => q
                .Bool(b =>
                {
                    b.Must(mb => mb
                        .MultiMatch(mm => mm
                            .Query(query)
                            .Type(TextQueryType.BoolPrefix)
                            .Fields(new[] { "title", "title._2gram", "title._3gram", "searchText^0.5" })));

                    if (kinds is { Count: > 0 })
                    {
                        b.Filter(f => f.Terms(t => t
                            .Field(d => d.Kind)
                            .Term(new TermsQueryField(kinds.Select(k => (FieldValue)k).ToArray()))));
                    }
                }))
            .Highlight(h => h.Fields(f => f
                .Add(new Field("title"), new HighlightFieldDescriptor<SearchDocument>())
                .Add(new Field("searchText"), new HighlightFieldDescriptor<SearchDocument>()))),
            cancellationToken);

        return response.Hits.Select(hit =>
        {
            var doc = hit.Source!;
            var matchedFields = hit.Highlight?.Keys.Select(k => k == "searchText" ? "searchText" : "title").ToArray()
                ?? [];
            if (matchedFields.Length == 0)
            {
                matchedFields = ["title"];
            }

            return new SearchHit(doc.Kind, doc.SourceId, doc.Title, doc.Subtitle, matchedFields, hit.Score ?? 0);
        }).ToList();
    }
}
