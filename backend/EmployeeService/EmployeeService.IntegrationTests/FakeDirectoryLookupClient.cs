using EmployeeService.Application.Directory;

namespace EmployeeService.IntegrationTests;

/// <summary>
/// Stands in for the real gRPC client to DirectoryService. These tests exercise
/// EmployeeService's own logic - the Employee domain, its repository, concurrency
/// and uniqueness against a real Postgres - not a live call to another service over
/// the network. A test that needed a real DirectoryService round trip would be a
/// different, heavier kind of test than what lives here.
/// </summary>
public sealed class FakeDirectoryLookupClient : IDirectoryLookupClient
{
    public static readonly AssignmentValidationResult ValidAssignment = new(
        DepartmentExists: true,
        DepartmentActive: true,
        DepartmentName: "Engineering",
        PositionExists: true,
        PositionActive: true,
        PositionName: "Engineer",
        PositionBelongsToDepartment: true);

    public AssignmentValidationResult NextValidation { get; set; } = ValidAssignment;

    public Task<DepartmentLookupResult> GetDepartmentAsync(Guid departmentId, CancellationToken cancellationToken) =>
        Task.FromResult(new DepartmentLookupResult(
            NextValidation.DepartmentExists, NextValidation.DepartmentName, NextValidation.DepartmentActive));

    public Task<PositionLookupResult> GetPositionAsync(Guid positionId, CancellationToken cancellationToken) =>
        Task.FromResult(new PositionLookupResult(
            NextValidation.PositionExists, NextValidation.PositionName, NextValidation.PositionActive));

    public Task<AssignmentValidationResult> ValidateAssignmentAsync(
        Guid departmentId,
        Guid positionId,
        CancellationToken cancellationToken) =>
        Task.FromResult(NextValidation);
}
