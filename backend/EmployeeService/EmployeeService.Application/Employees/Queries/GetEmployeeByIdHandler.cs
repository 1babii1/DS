using EmployeeService.Application.Database;
using Microsoft.EntityFrameworkCore;

namespace EmployeeService.Application.Employees.Queries;

public class GetEmployeeByIdHandler(IReadDbContext readDbContext)
{
    public async Task<EmployeeDto?> Handle(Guid employeeId, CancellationToken cancellationToken)
    {
        return await readDbContext.EmployeesRead
            .Where(e => e.Id == employeeId)
            .Select(e => new EmployeeDto(
                e.Id,
                e.FullName,
                e.Email,
                e.DepartmentId,
                e.DepartmentName,
                e.PositionId,
                e.PositionName,
                e.Status.ToString(),
                e.ProvisioningFailureReason,
                e.HiredAt))
            .SingleOrDefaultAsync(cancellationToken);
    }
}