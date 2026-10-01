using DirectoryService.Infrastructure.Postgres.Embeddings;
using Pgvector;

namespace DirectoryService.IntegrationTests;

// Stands in for Ollama: a vector that depends on the words of the text (each word bumps one of 768 slots), so two
// texts sharing words are close and texts sharing none are not. Deterministic, no model, no network.
public sealed class FakeEmbeddingClient : IEmbeddingClient
{
    private const int Dimensions = 768;

    public static Vector For(string text)
    {
        var values = new float[Dimensions];
        foreach (var word in text.ToLowerInvariant().Split(
                     [' ', '(', ')', ',', '.', '-'], StringSplitOptions.RemoveEmptyEntries))
        {
            values[Slot(word)] += 1;
        }

        var norm = MathF.Sqrt(values.Sum(v => v * v));
        if (norm == 0)
        {
            values[0] = 1;
            norm = 1;
        }

        return new Vector(values.Select(v => v / norm).ToArray());
    }

    public Task<Vector> EmbedAsync(string text, CancellationToken cancellationToken) => Task.FromResult(For(text));

    private static int Slot(string word)
    {
        var hash = 2166136261u;
        foreach (var character in word)
        {
            hash = (hash ^ character) * 16777619u;
        }

        return (int)(hash % Dimensions);
    }
}
