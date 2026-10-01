namespace SearchService.Domain;

// What a document is turned into a vector from. It is chosen per kind, and it is deliberately not the whole indexed
// text: an employee's SearchText ends with their email, and a vector cannot be un-read, so the email (and any other
// contact detail) never goes into the embedding model. Semantic search over people works by name and role.
public static class EmbeddingText
{
    public static string For(SearchDocument document) => document.Kind switch
    {
        SearchKind.Department => document.Subtitle is { Length: > 0 } identifier
            ? $"{document.Title} ({identifier})"
            : document.Title,
        SearchKind.Employee => document.Subtitle is { Length: > 0 } role
            ? $"{document.Title}, {role}"
            : document.Title,

        // Positions and locations index name, description or address and nothing personal.
        _ => document.SearchText.Trim(),
    };
}
