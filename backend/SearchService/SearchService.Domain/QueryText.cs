namespace SearchService.Domain;

// The keyword index matches any word of the query (a query of two words finds a name with either), which is what makes
// "marketing team" find "Marketing". The cost is that a word that means nothing on its own ("and", "the", "for")
// matches every name that happens to contain it: a search for "vendors and buyers" returned every department with an
// "and" in its name. Those words are dropped from what goes to the keyword index. The semantic side is given the query
// as typed: a model reads the whole sentence.
public static class QueryText
{
    private static readonly HashSet<string> JoiningWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "has", "have", "how", "in", "into", "is", "it",
        "of", "on", "or", "our", "that", "the", "their", "them", "there", "these", "this", "to", "us", "was", "we",
        "what", "when", "where", "which", "who", "why", "with", "you", "your",
    };

    public static string ForKeywordSearch(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = words.Where(w => !JoiningWords.Contains(w)).ToArray();

        // A query of nothing but such words is still a query: search for what was typed.
        return kept.Length == 0 ? query : string.Join(' ', kept);
    }
}
