using Microsoft.Extensions.Logging;

namespace EmployeeService.Application.Authorization;

public enum DepartmentAuthorizationMode
{
    /// <summary>What there was: anyone who may edit may hire into and transfer between any department.</summary>
    Roles = 0,

    /// <summary>Relationship-based (ADR 0057): a non-administrator may hire into, and transfer people in and out of, the departments they manage or whose ancestor they manage.</summary>
    Tree = 1,
}

public sealed class DepartmentAuthorizationOptions
{
    public const string SectionName = "DepartmentAuthorization";

    public DepartmentAuthorizationMode Mode { get; set; } = DepartmentAuthorizationMode.Roles;

    public string OpenFgaUrl { get; set; } = "http://openfga:8080";

    public string StoreName { get; set; } = "platform";

    /// <summary>How long a call to OpenFGA may take before the decision is "unavailable". A check is one small query; this is generous.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);
}

public enum AccessDecision
{
    Allowed,
    Denied,

    /// <summary>The question could not be answered. Treated as a refusal by every caller, and told apart from one so that it is a 503 and not a 403.</summary>
    Unavailable,
}

/// <summary>Who is asking, read from the token and nowhere else.</summary>
public sealed record Caller(Guid? AccountId, bool IsAdmin);

public interface IDepartmentAuthorization
{
    Task<AccessDecision> CanManageAsync(Caller caller, Guid departmentId, CancellationToken cancellationToken);
}

/// <summary>One tuple of the authorization store: <c>user</c> has <c>relation</c> to <c>object</c>.</summary>
public sealed record FgaTuple(string User, string Relation, string Object)
{
    public static string UserId(Guid accountId) => $"user:{accountId:D}";

    public static string DepartmentId(Guid departmentId) => $"department:{departmentId:D}";
}

/// <summary>The authorization store (OpenFGA). A port so that decisions and the tree's upkeep are tested without a server, and with one in a separate test.</summary>
public interface IFgaClient
{
    Task<bool> CheckAsync(FgaTuple tuple, CancellationToken cancellationToken);

    /// <summary>One atomic request: the writes and the deletes happen together or not at all. A write of a tuple that exists, and a delete of one that does not, are not errors.</summary>
    Task WriteAsync(IReadOnlyCollection<FgaTuple> writes, IReadOnlyCollection<FgaTuple> deletes, CancellationToken cancellationToken);

    /// <summary>The tuples whose object is <paramref name="obj"/>, optionally of one relation.</summary>
    Task<IReadOnlyList<FgaTuple>> ReadAsync(string obj, string? relation, CancellationToken cancellationToken);
}

/// <summary>Roles mode: the authorization of before; this adds no condition.</summary>
public sealed class RolesDepartmentAuthorization : IDepartmentAuthorization
{
    public Task<AccessDecision> CanManageAsync(Caller caller, Guid departmentId, CancellationToken cancellationToken) =>
        Task.FromResult(AccessDecision.Allowed);
}

/// <summary>
/// Tree mode (ADR 0057). An administrator is allowed without asking: an outage of the store must not lock the people who would repair it
/// out of hiring. Anyone else is allowed only if the store says they manage the department or one above it, and **if the store cannot say, the
/// answer is no**: a refusal that is reported as "unavailable", never a guess and never an allow.
/// </summary>
public sealed class TreeDepartmentAuthorization(IFgaClient store, ILogger<TreeDepartmentAuthorization> logger) : IDepartmentAuthorization
{
    public const string CanManage = "can_manage";

    public async Task<AccessDecision> CanManageAsync(Caller caller, Guid departmentId, CancellationToken cancellationToken)
    {
        if (caller.IsAdmin)
        {
            return AccessDecision.Allowed;
        }

        if (caller.AccountId is not { } account)
        {
            return AccessDecision.Denied;
        }

        try
        {
            var allowed = await store.CheckAsync(
                new FgaTuple(FgaTuple.UserId(account), CanManage, FgaTuple.DepartmentId(departmentId)), cancellationToken);
            return allowed ? AccessDecision.Allowed : AccessDecision.Denied;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not ask the authorization store whether {Account} manages department {Department}; refusing", account, departmentId);
            return AccessDecision.Unavailable;
        }
    }
}
