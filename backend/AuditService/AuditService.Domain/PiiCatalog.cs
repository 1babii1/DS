namespace AuditService.Domain;

/// <summary>
/// Which fields of which events are personal data (docs/security/pii-inventory.json, ADR 0043), and so are stored encrypted under a
/// key of the person they are about (ADR 0046). A test requires this list to equal the inventory, so that a field declared personal
/// there cannot be left stored in the clear here.
/// </summary>
public static class PiiCatalog
{
    private static readonly Dictionary<string, string[]> Fields = new(StringComparer.Ordinal)
    {
        ["EmployeeHired"] = ["FullName", "Email"],
        ["AccountDeleted"] = ["Email", "IpAddress"],
        ["AccountLockedOut"] = ["Email", "IpAddress"],
        ["AdminAccountLocked"] = ["TargetEmail", "IpAddress"],
        ["AdminAccountUnlocked"] = ["TargetEmail", "IpAddress"],
        ["AdminRolesChanged"] = ["TargetEmail", "IpAddress"],
        ["AllSessionsRevoked"] = ["Email", "IpAddress"],
        ["EmailChanged"] = ["OldEmail", "NewEmail", "IpAddress"],
        ["LoginFailed"] = ["Email", "IpAddress"],
        ["LoginSucceeded"] = ["Email", "IpAddress"],
        ["PasswordChanged"] = ["Email", "IpAddress"],
    };

    public static IReadOnlyDictionary<string, string[]> FieldsByEvent => Fields;

    public static string[] For(string eventType) => Fields.TryGetValue(eventType, out var fields) ? fields : [];
}
