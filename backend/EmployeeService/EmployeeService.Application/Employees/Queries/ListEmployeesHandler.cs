using EmployeeService.Application.Database;
using Microsoft.EntityFrameworkCore;
using Shared;

namespace EmployeeService.Application.Employees.Queries;

/// <summary>
/// Страница списка сотрудников. Раньше отдавался весь список целиком: с ростом
/// штата это и загрузка всей таблицы в память на каждый запрос, и выдача адресов
/// электронной почты всех сотрудников одним вызовом.
/// </summary>
public class ListEmployeesHandler(IReadDbContext readDbContext)
{
    public async Task<PagedResponse<EmployeeDto>> Handle(
        Guid? departmentId,
        int? page,
        int? size,
        CancellationToken cancellationToken)
    {
        var (currentPage, pageSize) = PagedResponse<EmployeeDto>.Normalize(page, size);

        var query = readDbContext.EmployeesRead;

        if (departmentId is not null)
        {
            query = query.Where(e => e.DepartmentId == departmentId);
        }

        var total = await query.CountAsync(cancellationToken);
        if (total == 0)
        {
            return PagedResponse<EmployeeDto>.Empty(currentPage, pageSize);
        }

        var items = await query
            .OrderBy(e => e.FullName)
            .ThenBy(e => e.Id)
            .Skip((currentPage - 1) * pageSize)
            .Take(pageSize)
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

        return new PagedResponse<EmployeeDto>(items, currentPage, pageSize, total);
    }
}