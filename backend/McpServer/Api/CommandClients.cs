using System.Net.Http.Json;
using System.Text.Json;

namespace McpServer.Api;

// Writes, as the caller (BearerForwardingHandler is on these clients too). Registered with a pipeline that
// has NO retry: repeating a write blindly is how one request becomes two effects. Where a service supports
// an Idempotency-Key the key is what makes a repeat safe, and it is sent; everywhere else a failure is
// reported, not retried.
public sealed class EmployeeCommandClient(HttpClient http)
{
    public async Task<Guid> HireAsync(
        string fullName, string email, Guid departmentId, Guid positionId, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            "api/employees", new { fullName, email, departmentId, positionId }, ct);
        return await CommandResponse.ReadIdAsync(response, ct);
    }

    public async Task TransferAsync(Guid employeeId, Guid departmentId, Guid positionId, CancellationToken ct)
    {
        using var response = await http.PutAsJsonAsync(
            $"api/employees/{employeeId}/transfer", new { departmentId, positionId }, ct);
        CommandResponse.EnsureSuccess(response);
    }
}

// Grants an assistant makes go to their own route in RewardsService, which has the assistant's limits (a ceiling per
// grant and a daily quota) and records where the money came from. The manual grant route is not used from here.
public sealed class RewardsCommandClient(HttpClient http)
{
    public async Task<Guid> GrantAsync(
        Guid employeeId, decimal amount, string reason, string idempotencyKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/rewards/agent-grants")
        {
            Content = JsonContent.Create(new { employeeId, amount, reason }),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        using var response = await http.SendAsync(request, ct);
        return await CommandResponse.ReadIdAsync(response, ct);
    }
}

internal static class CommandResponse
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw ServiceApiException.From(response.StatusCode);
        }
    }

    // Success envelope: { "result": "<guid>", "isError": false }. The body of a failure is never read.
    public static async Task<Guid> ReadIdAsync(HttpResponseMessage response, CancellationToken ct)
    {
        EnsureSuccess(response);

        var envelope = await response.Content.ReadFromJsonAsync<IdEnvelope>(Json, ct);
        if (envelope is null || envelope.IsError || envelope.Result is not { } id || id == Guid.Empty)
        {
            throw ServiceApiException.From(System.Net.HttpStatusCode.InternalServerError);
        }

        return id;
    }

    private sealed record IdEnvelope(Guid? Result, bool IsError);
}
