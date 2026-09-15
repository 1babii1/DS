namespace McpServer.Embeddings;

public sealed class EmbeddingsOptions
{
    public const string SectionName = "Embeddings";

    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    public string Model { get; set; } = "nomic-embed-text";
}