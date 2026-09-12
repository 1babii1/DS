using DirectoryService.Grpc;
using EmployeeService.Application.Directory;

namespace EmployeeService.Infrastructure.DirectoryGrpc;

public class DirectoryLookupClient(DirectoryLookup.DirectoryLookupClient client) : IDirectoryLookupClient
{
    public async Task<DepartmentLookupResult> GetDepartmentAsync(Guid departmentId, CancellationToken cancellationToken)
    {
        var reply = await client.GetDepartmentAsync(
            new GetDepartmentRequest { DepartmentId = departmentId.ToString() },
            cancellationToken: cancellationToken);

        return new DepartmentLookupResult(reply.Found, reply.Name, reply.IsActive);
    }

    public async Task<PositionLookupResult> GetPositionAsync(Guid positionId, CancellationToken cancellationToken)
    {
        var reply = await client.GetPositionAsync(
            new GetPositionRequest { PositionId = positionId.ToString() },
            cancellationToken: cancellationToken);

        return new PositionLookupResult(reply.Found, reply.Name, reply.IsActive);
    }

    public async Task<AssignmentValidationResult> ValidateAssignmentAsync(
        Guid departmentId,
        Guid positionId,
        CancellationToken cancellationToken)
    {
        var reply = await client.ValidateAssignmentAsync(
            new ValidateAssignmentRequest
            {
                DepartmentId = departmentId.ToString(),
                PositionId = positionId.ToString(),
            },
            cancellationToken: cancellationToken);

        return new AssignmentValidationResult(
            reply.DepartmentExists,
            reply.DepartmentActive,
            reply.DepartmentName,
            reply.PositionExists,
            reply.PositionActive,
            reply.PositionName,
            reply.PositionBelongsToDepartment);
    }
}
