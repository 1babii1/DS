namespace DirectoryService.Infrastructure.Postgres.Embeddings;

public sealed class EmbeddingsOptions
{
    public const string SectionName = "Embeddings";

    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    public string Model { get; set; } = "nomic-embed-text";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);

    public int BatchSize { get; set; } = 20;
}
