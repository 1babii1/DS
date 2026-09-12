using EmployeeService.Domain;

namespace EmployeeService.Application.Database;

public interface IReadDbContext
{
    IQueryable<Employee> EmployeesRead { get; }
}
