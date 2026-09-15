namespace Shared.Security;

/// <summary>
/// Role names as they appear in the "role" claim every service validates the same
/// JWT against. Not shared with AuthService.Domain, which owns its own identical
/// copy on purpose - the Domain layer there has no dependency on Shared, and
/// adding one just to remove three string literals would trade a real boundary
/// for a cosmetic one. This copy is for the Web-layer policy registrations in the
/// resource servers (DirectoryService, EmployeeService), which already depend on
/// Shared and previously spelled these out as raw literals.
/// </summary>
public static class RoleNames
{
    public const string Admin = "admin";

    public const string Editor = "editor";

    public const string Viewer = "viewer";
}