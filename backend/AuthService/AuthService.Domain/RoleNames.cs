namespace AuthService.Domain;

public static class RoleNames
{
    public const string Admin = "admin";

    public const string Editor = "editor";

    public const string Viewer = "viewer";

    public static readonly IReadOnlyCollection<string> All = [Admin, Editor, Viewer];
}
