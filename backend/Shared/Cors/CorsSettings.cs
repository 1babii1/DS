namespace Shared.Cors;

public class CorsSettings
{
    public const string CORS = "Cors";

    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    public bool AllowCredentials { get; init; } = true;

    public IReadOnlyList<string> AllowedHeaders { get; init; } = [];

    public IReadOnlyList<string> AllowedMethods { get; init; } = [];
}