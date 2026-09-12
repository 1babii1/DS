using EmployeeService.Application.Database;
using Microsoft.EntityFrameworkCore;

namespace EmployeeService.Application.Employees.Queries;

public class ListEmployeesHandler(IReadDbContext readDbContext)
{
    public async Task<List<EmployeeDto>> Handle(Guid? departmentId, CancellationToken cancellationToken)
    {
        var query = readDbContext.EmployeesRead.AsQueryable();

        if (departmentId is not null)
        {
            query = query.Where(e => e.DepartmentId == departmentId);
        }

        return await query
            .OrderBy(e => e.FullName)
            .Select(e => new EmployeeDto(
                e.Id,
                e.FullName,
                e.Email,
                e.DepartmentId,
                e.DepartmentName,
                e.PositionId,
                e.PositionName,
                e.Status.ToString(),
                e.HiredAt))
            .ToListAsync(cancellationToken);
    }
}
