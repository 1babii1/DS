namespace SearchService.Domain;

// String, not a numeric enum: this value is serialized straight into Elasticsearch
// documents and the public API's "kind"/"types" values - a numeric enum would leak
// implementation-detail integers into the response contract.
public static class SearchKind
{
    public const string Employee = "employee";
    public const string Department = "department";
    public const string Position = "position";
    public const string Location = "location";
    public const string Audit = "audit";

    public static readonly IReadOnlyList<string> All = [Employee, Department, Position, Location, Audit];

    public static bool IsValid(string kind) => All.Contains(kind, StringComparer.OrdinalIgnoreCase);
}
