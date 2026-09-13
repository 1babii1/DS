using DirectoryService.Application.Database;
using DirectoryService.Domain.Departments.ValueObjects;
using DirectoryService.Domain.Positions.ValueObjects;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DirectoryService.Grpc;

// Internal-only, but still authenticated: the port is not reachable from outside the
// compose network, and that is a deployment detail, not an authorization boundary.
[Authorize]
public class DirectoryLookupService(IReadDbContext readDbContext) : DirectoryLookup.DirectoryLookupBase
{
    public override async Task<DepartmentReply> GetDepartment(GetDepartmentRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.DepartmentId, out var departmentId))
        {
            return new DepartmentReply { Found = false };
        }

        var department = await readDbContext.DepartmentsRead
            .SingleOrDefaultAsync(d => d.Id == DepartmentId.FromValue(departmentId), context.CancellationToken);

        if (department is null)
        {
            return new DepartmentReply { Found = false };
        }

        return new DepartmentReply
        {
            Found = true,
            Id = department.Id.Value.ToString(),
            Name = department.Name.Value,
            IsActive = department.IsActive,
        };
    }

    public override async Task<PositionReply> GetPosition(GetPositionRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.PositionId, out var positionId))
        {
            return new PositionReply { Found = false };
        }

        var position = await readDbContext.PositionsRead
            .SingleOrDefaultAsync(p => p.Id == PositionId.FromValue(positionId), context.CancellationToken);

        if (position is null)
        {
            return new PositionReply { Found = false };
        }

        return new PositionReply
        {
            Found = true,
            Id = position.Id.Value.ToString(),
            Name = position.Name.Value,
            IsActive = position.IsActive,
        };
    }

    public override async Task<ValidateAssignmentReply> ValidateAssignment(
        ValidateAssignmentRequest request,
        ServerCallContext context)
    {
        var reply = new ValidateAssignmentReply();

        if (!Guid.TryParse(request.DepartmentId, out var departmentGuid) ||
            !Guid.TryParse(request.PositionId, out var positionGuid))
        {
            return reply;
        }

        var departmentId = DepartmentId.FromValue(departmentGuid);
        var positionId = PositionId.FromValue(positionGuid);

        var department = await readDbContext.DepartmentsRead
            .SingleOrDefaultAsync(d => d.Id == departmentId, context.CancellationToken);
        var position = await readDbContext.PositionsRead
            .SingleOrDefaultAsync(p => p.Id == positionId, context.CancellationToken);

        reply.DepartmentExists = department is not null;
        reply.DepartmentActive = department?.IsActive ?? false;
        reply.DepartmentName = department?.Name.Value ?? string.Empty;

        reply.PositionExists = position is not null;
        reply.PositionActive = position?.IsActive ?? false;
        reply.PositionName = position?.Name.Value ?? string.Empty;

        reply.PositionBelongsToDepartment = await readDbContext.DepartmentsPositionsRead
            .AnyAsync(
                dp => dp.DepartmentId == departmentId && dp.PositionId == positionId,
                context.CancellationToken);

        return reply;
    }
}