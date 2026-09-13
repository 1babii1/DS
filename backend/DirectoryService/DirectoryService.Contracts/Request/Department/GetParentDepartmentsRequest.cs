namespace DirectoryService.Contracts.Request.Department;

/// <param name="Preferch">
/// Misspelling of "prefetch", kept as-is deliberately: it is a public query
/// parameter name and the frontend already binds to it. Renaming it would be a
/// breaking API change for a cosmetic gain - not worth it without coordinating
/// a frontend update at the same time.
/// </param>
public record GetParentDepartmentsRequest(int? Page = 1, int? Size = 20, int? Preferch = 3);
