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

    // Only set when Status is ProvisioningFailed - the reason AuthService
    // reported for why the login account couldn't be created (e.g. a
    // duplicate email), surfaced to whoever needs to retry the hire.
    public string? ProvisioningFailureReason { get; private set; }

    // Null for employees hired before actor tracking existed - genuinely
    // unknown, not an oversight, so nullable rather than backfilled. Lets
    // AccountProvisioningFailed notify whoever did the hiring; a null here
    // just means that notification is skipped, not an error.
    public Guid? HiredByAccountId { get; private set; }

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
        string positionName,
        Guid? hiredByAccountId = null)
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
            Id = Guid.CreateVersion7(),
            FullName = fullName,
            Email = email,
            DepartmentId = departmentId,
            DepartmentName = departmentName,
            PositionId = positionId,
            PositionName = positionName,
            Status = EmployeeStatus.PendingProvisioning,
            HiredByAccountId = hiredByAccountId,
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

    // Called from AuthEventsConsumer on AccountProvisioned. Only ever leaves
    // PendingProvisioning - already Active (a redelivered event) or any other
    // status is a no-op, not an error, so an at-least-once Kafka consumer can
    // safely process the same event twice.
    public void CompleteProvisioning()
    {
        if (Status != EmployeeStatus.PendingProvisioning)
        {
            return;
        }

        Status = EmployeeStatus.Active;
        UpdatedAt = DateTime.UtcNow;
    }

    // Called from AuthEventsConsumer on AccountProvisioningFailed - the
    // compensating step of the Hire Employee saga. Same idempotency reasoning
    // as CompleteProvisioning: redelivery of the same event is a safe no-op.
    public void FailProvisioning(string reason)
    {
        if (Status != EmployeeStatus.PendingProvisioning)
        {
            return;
        }

        Status = EmployeeStatus.ProvisioningFailed;
        ProvisioningFailureReason = reason;
        UpdatedAt = DateTime.UtcNow;
    }

    // Called when the onboarding process (HireSaga, ADR 0032) gives up on a hire: the account or the welcome bonus did not
    // arrive in time. From any state except Terminated (a person already let go stays that way); idempotent, as every step of
    // this process has to be.
    public void CompensateOnboarding(string reason)
    {
        if (Status is EmployeeStatus.Terminated or EmployeeStatus.ProvisioningFailed)
        {
            return;
        }

        Status = EmployeeStatus.ProvisioningFailed;
        ProvisioningFailureReason = reason.Length > 500 ? reason[..500] : reason;
        UpdatedAt = DateTime.UtcNow;
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

    public const string ErasedName = "[erased]";

    // Erasure of the person's data at the service that owns it (ADR 0048). The row stays, so ids held elsewhere still point at something
    // and the history of what happened (status, department, dates) is intact; what identifies the person is replaced. The address stays
    // unique per person by carrying the id. Only for someone no longer working here, and idempotent.
    public UnitResult<Error> Erase()
    {
        if (Status is not (EmployeeStatus.Terminated or EmployeeStatus.ProvisioningFailed))
        {
            return Error.Conflict("employee.erase.still_active", "Only an employee who has left (or whose hire failed) can be erased; terminate first");
        }

        FullName = ErasedName;
        Email = $"erased-{Id:N}@erased.invalid";
        ProvisioningFailureReason = null;
        UpdatedAt = DateTime.UtcNow;

        return UnitResult.Success<Error>();
    }
}
