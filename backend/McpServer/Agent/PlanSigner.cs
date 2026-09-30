using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace McpServer.Agent;

public enum PlanTokenProblem
{
    Malformed,
    BadSignature,
    Expired,
    WrongUser,
}

public sealed class PlanTokenException(PlanTokenProblem problem) : Exception(problem.ToString())
{
    public PlanTokenProblem Problem { get; } = problem;
}

// A plan is a signed, self-contained token, not a row in a database: McpServer owns no storage, and a
// signature is enough to prove the plan is the one that was shown to this user and has not been altered.
// Consequence, stated rather than hidden: a plan is valid on whichever instances share the signing key,
// and confirming the same token twice re-runs its steps - each service's own idempotency (Idempotency-Key
// on grants, unique email on hire) is what stops a second effect, not this class.
public sealed class PlanSigner
{
    private const string Version = "v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly byte[] _key;
    private readonly TimeProvider _clock;

    public PlanSigner(byte[] key, TimeProvider clock)
    {
        if (key.Length < 32)
        {
            throw new ArgumentException("The plan signing key must be at least 32 bytes.", nameof(key));
        }

        _key = key;
        _clock = clock;
    }

    public string Sign(Plan plan)
    {
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(plan, Json));
        return $"{Version}.{payload}.{Base64Url(Mac(payload))}";
    }

    // Order matters: authenticity first, then shape, then expiry, then who is asking. Nothing inside a
    // token is read as a plan until its signature has been checked.
    public Plan Verify(string token, Guid callerUserId)
    {
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0] != Version)
        {
            throw new PlanTokenException(PlanTokenProblem.Malformed);
        }

        byte[] presented;
        try
        {
            presented = FromBase64Url(parts[2]);
        }
        catch (FormatException)
        {
            throw new PlanTokenException(PlanTokenProblem.Malformed);
        }

        if (!CryptographicOperations.FixedTimeEquals(presented, Mac(parts[1])))
        {
            throw new PlanTokenException(PlanTokenProblem.BadSignature);
        }

        Plan? plan;
        try
        {
            plan = JsonSerializer.Deserialize<Plan>(FromBase64Url(parts[1]), Json);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new PlanTokenException(PlanTokenProblem.Malformed);
        }

        if (plan is null || plan.Steps.Count == 0)
        {
            throw new PlanTokenException(PlanTokenProblem.Malformed);
        }

        if (_clock.GetUtcNow() >= plan.ExpiresAt)
        {
            throw new PlanTokenException(PlanTokenProblem.Expired);
        }

        if (plan.UserId != callerUserId)
        {
            throw new PlanTokenException(PlanTokenProblem.WrongUser);
        }

        return plan;
    }

    private byte[] Mac(string payload) => HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(payload));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
        return Convert.FromBase64String(s);
    }
}
