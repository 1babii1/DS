using DirectoryService.Grpc;
using EmployeeService.Application.Directory;
using Grpc.Core;

namespace EmployeeService.Infrastructure.DirectoryGrpc;

public class DirectoryLookupClient(DirectoryLookup.DirectoryLookupClient client) : IDirectoryLookupClient
{
    // Адаптер - единственное место, знающее про gRPC, поэтому он же переводит статусы
    // транспорта в причины, которыми оперирует Application.
    private static async Task<T> TranslateFailures<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied)
        {
            throw new DirectoryLookupException(
                DirectoryLookupFailure.Unauthorized,
                "DirectoryService rejected the forwarded credentials",
                ex);
        }
        catch (RpcException ex)
        {
            throw new DirectoryLookupException(
                DirectoryLookupFailure.Unavailable,
                $"DirectoryService call failed with {ex.StatusCode}",
                ex);
        }
    }

    public async Task<DepartmentLookupResult> GetDepartmentAsync(Guid departmentId, CancellationToken cancellationToken)
    {
        var reply = await TranslateFailures(() => client.GetDepartmentAsync(
            new GetDepartmentRequest { DepartmentId = departmentId.ToString() },
            cancellationToken: cancellationToken).ResponseAsync);

        return new DepartmentLookupResult(reply.Found, reply.Name, reply.IsActive);
    }

    public async Task<PositionLookupResult> GetPositionAsync(Guid positionId, CancellationToken cancellationToken)
    {
        var reply = await TranslateFailures(() => client.GetPositionAsync(
            new GetPositionRequest { PositionId = positionId.ToString() },
            cancellationToken: cancellationToken).ResponseAsync);

        return new PositionLookupResult(reply.Found, reply.Name, reply.IsActive);
    }

    public async Task<AssignmentValidationResult> ValidateAssignmentAsync(
        Guid departmentId,
        Guid positionId,
        CancellationToken cancellationToken)
    {
        var reply = await TranslateFailures(() => client.ValidateAssignmentAsync(
            new ValidateAssignmentRequest
            {
                DepartmentId = departmentId.ToString(),
                PositionId = positionId.ToString(),
            },
            cancellationToken: cancellationToken).ResponseAsync);

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
