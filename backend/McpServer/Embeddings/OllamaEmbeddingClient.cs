using System.Net.Http.Json;
using Pgvector;

namespace McpServer.Embeddings;

// Deliberate small duplicate of DirectoryService's Ollama client rather than a
// cross-service assembly reference - MCP server only needs to embed a query
// string, and this keeps it independently deployable.
public sealed class OllamaEmbeddingClient(HttpClient httpClient)
{
    public async Task<Vector> EmbedAsync(string model, string text, CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync(
            "/api/embeddings",
            new OllamaEmbeddingRequest(model, text),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Ollama returned an empty embeddings response.");

        return new Vector(payload.Embedding);
    }

    private sealed record OllamaEmbeddingRequest(string Model, string Prompt);

    private sealed record OllamaEmbeddingResponse(float[] Embedding);
}
