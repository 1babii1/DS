using System.Net.Http.Json;
using System.Text.Json;
using McpServer.Tools;

namespace McpServer.Api;

// Reads the organization as it was at a past instant from AuditService, which rebuilds it from its event log. A read
// like every other here: as the caller, through the service's own API, so what it returns is what that person could
// ask for directly.
public sealed class AuditApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<OrgSnapshotDto> OrgChartAsync(string at, CancellationToken ct)
    {
        using var response = await http.GetAsync($"api/audit/org-chart?at={Uri.EscapeDataString(at)}", ct);
        if (!response.IsSuccessStatusCode)
        {
            throw ServiceApiException.From(response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<OrgSnapshotDto>(Json, ct)
            ?? throw ServiceApiException.From(System.Net.HttpStatusCode.InternalServerError);
    }
}

public sealed record OrgSnapshotDto(
    DateTime At, IReadOnlyList<OrgSnapshotDepartment> Departments, IReadOnlyList<OrgSnapshotPerson> Unplaced, int SkippedEvents);

public sealed record OrgSnapshotDepartment(
    Guid Id, Guid? ParentId, string Name, string Identifier, int Depth, IReadOnlyList<OrgSnapshotPerson> People);

public sealed record OrgSnapshotPerson(Guid Id, string Name, string Position);
