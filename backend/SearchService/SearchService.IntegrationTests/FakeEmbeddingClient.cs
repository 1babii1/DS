using Pgvector;
using SearchService.Infrastructure.Postgres.Embeddings;

namespace SearchService.IntegrationTests;

// Stands in for Ollama: a vector that depends on the words of the text (each word bumps one of 768 slots), so two
// texts sharing words are close and texts sharing none are not. Deterministic, no model, no network. Can be told to
// fail, to show that a broken model leaves work pending instead of breaking anything else.
public sealed class FakeEmbeddingClient : IEmbeddingClient
{
    private const int Dimensions = 768;

    public static bool Fail { get; set; }

    // Runs while a vector is being made, to let a test change the world in the middle of an embedding call.
    public static Action? DuringEmbed { get; set; }

    public static Vector For(string text)
    {
        var values = new float[Dimensions];
        foreach (var word in text.ToLowerInvariant().Split(
                     [' ', '(', ')', ',', '.', '-', '·'], StringSplitOptions.RemoveEmptyEntries))
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

    public Task<Vector> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new HttpRequestException("model is down");
        }

        DuringEmbed?.Invoke();
        return Task.FromResult(For(text));
    }

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
