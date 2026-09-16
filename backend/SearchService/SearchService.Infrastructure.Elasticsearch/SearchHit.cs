namespace SearchService.Infrastructure.Elasticsearch;

public record SearchHit(string Kind, Guid SourceId, string Title, string? Subtitle, string[] MatchedFields, double Rank);
