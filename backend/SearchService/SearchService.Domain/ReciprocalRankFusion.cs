namespace SearchService.Domain;

// Reciprocal Rank Fusion: several ranked lists become one by summing, for each item, 1 / (k + rank) over the lists it
// appears in (rank counts from 1). It uses only positions, so two sources whose scores mean different things (a text
// relevance and a cosine distance) can be combined without normalising either. An item that both lists put in the
// middle beats one that only a single list puts first, which is the point of combining them.
public static class ReciprocalRankFusion
{
    // The constant from the original paper; larger k flattens the difference between the top ranks and the rest.
    public const int DefaultK = 60;

    /// <param name="rankedLists">Each list is ordered best first and holds keys that identify the same item across lists.</param>
    /// <returns>Keys with their fused score, best first. Ties keep the item whose best rank was better, then key order.</returns>
    public static IReadOnlyList<(string Key, double Score)> Fuse(
        IReadOnlyList<IReadOnlyList<string>> rankedLists, int k = DefaultK)
    {
        var scores = new Dictionary<string, (double Score, int BestRank)>();
        foreach (var list in rankedLists)
        {
            var seen = new HashSet<string>();
            for (var index = 0; index < list.Count; index++)
            {
                var key = list[index];
                if (!seen.Add(key))
                {
                    continue;
                }

                var rank = index + 1;
                var (score, best) = scores.TryGetValue(key, out var current) ? current : (0d, int.MaxValue);
                scores[key] = (score + 1d / (k + rank), Math.Min(best, rank));
            }
        }

        return scores
            .OrderByDescending(pair => pair.Value.Score)
            .ThenBy(pair => pair.Value.BestRank)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (pair.Key, pair.Value.Score))
            .ToList();
    }
}
