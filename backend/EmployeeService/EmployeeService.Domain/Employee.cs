using CSharpFunctionalExtensions;
using Shared;

namespace EmployeeService.Domain;

public class Employee
{
    public Guid Id { get; private set; }

    public string FullName { get; private set; } = null!;

    public string Email { get; private set; } = null!;

    public Guid DepartmentId { get; private set; }

    // Denormalized snapshot from DirectoryService, refreshed on every assignment
    // change - avoids a gRPC round trip on every employee read.
    public string DepartmentName { get; private set; } = null!;

    public Guid PositionId { get; private set; }

    public string PositionName { get; private set; } = null!;

    public EmployeeStatus Status { get; private set; }

    public DateTime HiredAt { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    private Employee()
    {
    }

    public static Result<Employee, Error> Hire(
        string fullName,
        string email,
        Guid departmentId,
        string departmentName,
        Guid positionId,
        string positionName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return Error.Validation("employee.full_name.empty", "Full name is required", "fullName");
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            return Error.Validation("employee.email.empty", "Email is required", "email");
        }

        var now = DateTime.UtcNow;

        return new Employee
        {
            Id = Guid.NewGuid(),
            FullName = fullName,
            Email = email,
            DepartmentId = departmentId,
            DepartmentName = departmentName,
            PositionId = positionId,
            PositionName = positionName,
            Status = EmployeeStatus.Active,
            HiredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public UnitResult<Error> Transfer(Guid departmentId, string departmentName, Guid positionId, string positionName)
    {
        if (Status != EmployeeStatus.Active)
        {
            return Error.Conflict("employee.transfer.not_active", "Only active employees can be transferred");
        }

        DepartmentId = departmentId;
        DepartmentName = departmentName;
        PositionId = positionId;
        PositionName = positionName;
        UpdatedAt = DateTime.UtcNow;

        return UnitResult.Success<Error>();
    }

    public UnitResult<Error> Terminate()
    {
        if (Status == EmployeeStatus.Terminated)
        {
            return Error.Conflict("employee.terminate.already_terminated", "Employee is already terminated");
        }

        Status = EmployeeStatus.Terminated;
        UpdatedAt = DateTime.UtcNow;

        return UnitResult.Success<Error>();
    }
}
