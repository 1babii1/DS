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

    // How long a call takes, honouring cancellation like a real HTTP call would.
    public static TimeSpan Delay { get; set; }

    // Words a real model would place close together: each group shares one slot, so a query using one word finds a
    // document using another, which a keyword match cannot.
    private static readonly Dictionary<string, string> Synonyms = new[]
    {
        new[] { "invoices", "invoice", "billing", "bills" },
        new[] { "hiring", "recruitment", "talent" },
    }.SelectMany(group => group.Select(word => (word, canonical: group[0]))).ToDictionary(x => x.word, x => x.canonical);

    public async Task<Vector> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new HttpRequestException("model is down");
        }

        DuringEmbed?.Invoke();
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        return For(text);
    }

    private static int Slot(string word)
    {
        word = Synonyms.GetValueOrDefault(word, word);
        var hash = 2166136261u;
        foreach (var character in word)
        {
            hash = (hash ^ character) * 16777619u;
        }

        return (int)(hash % Dimensions);
    }
}

public static class FakeEmbedderCollection
{
    public const string Name = "fake embedder";
}

[CollectionDefinition(FakeEmbedderCollection.Name)]
public sealed class FakeEmbedderCollectionDefinition;
