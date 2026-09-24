using System.Net.Http.Json;
using System.Text.Json;
using McpServer.Tools;
using Shared;

namespace McpServer.Api;

public sealed class EmployeeApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<EmployeeDetails?> GetAsync(Guid employeeId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"api/employees/{employeeId}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw ServiceApiException.From(response.StatusCode);
        }

        var dto = await response.Content.ReadFromJsonAsync<EmployeeDto>(Json, ct);
        return dto is null ? null : ToDetails(dto);
    }

    // One page, sized by the API's own rules (default 20, at most PagedResponse.MaxSize). The API
    // is paged on purpose - it used to return the whole list and with it every employee's email in
    // a single call - so a tool built on it inherits that bound instead of reopening it.
    public async Task<PagedResponse<EmployeeDetails>> ListByDepartmentAsync(
        Guid departmentId, int page, int size, CancellationToken ct)
    {
        using var response = await http.GetAsync(
            $"api/employees?departmentId={departmentId}&page={page}&size={size}", ct);
        if (!response.IsSuccessStatusCode)
        {
            throw ServiceApiException.From(response.StatusCode);
        }

        var paged = await response.Content.ReadFromJsonAsync<PagedDto>(Json, ct)
            ?? throw ServiceApiException.From(System.Net.HttpStatusCode.InternalServerError);
        return new PagedResponse<EmployeeDetails>(
            paged.Items.Select(ToDetails).ToList(), paged.Page, paged.Size, paged.Total);
    }

    private static EmployeeDetails ToDetails(EmployeeDto d) => new(
        d.Id, d.FullName, d.Email, d.DepartmentId, d.DepartmentName, d.PositionId, d.PositionName, d.Status, d.HiredAt);

    private sealed record PagedDto(List<EmployeeDto> Items, int Page, int Size, int Total);

    private sealed record EmployeeDto(
        Guid Id, string FullName, string Email, Guid DepartmentId, string DepartmentName,
        Guid PositionId, string PositionName, string Status, DateTime HiredAt);
}
