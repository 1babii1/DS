namespace AuthService.Web.Pages.Shared;

public sealed record PasswordInputModel(
    string Autocomplete,
    string ErrorId,
    string Id,
    string Label,
    string Name);
