using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Pgvector;

namespace DirectoryService.Infrastructure.Postgres.Embeddings;

public sealed class OllamaEmbeddingClient(HttpClient httpClient, IOptions<EmbeddingsOptions> options) : IEmbeddingClient
{
    private readonly EmbeddingsOptions _options = options.Value;

    public async Task<Vector> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync(
            "/api/embeddings",
            new OllamaEmbeddingRequest(_options.Model, text),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Ollama returned an empty embeddings response.");

        return new Vector(payload.Embedding);
    }

    private sealed record OllamaEmbeddingRequest(string Model, string Prompt);

    private sealed record OllamaEmbeddingResponse(float[] Embedding);
}
